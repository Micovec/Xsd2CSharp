using System.Linq;
using System.Xml;
using System.Xml.Schema;
using Xsd2CSharp.Core.Model;
using Xsd2CSharp.Core.Naming;

namespace Xsd2CSharp.Core.Xsd;

/// <summary>
/// Walks a compiled <see cref="XmlSchemaSet"/> and builds a <see cref="SchemaModel"/> describing
/// the C# types to generate.
///
/// Scope ("practical core subset"): element, complexType (sequence/choice/all with trivial
/// (1,1) nesting), attribute, simpleType restriction (enumeration incl. NMTOKEN/NMTOKENS,
/// other facets are otherwise ignored), simpleType list/union, minOccurs/maxOccurs,
/// xsd:import/include (via XmlSchemaSet), simpleContent extension (flattened into one class -
/// see HasSimpleContentExtension), complexContent extension/restriction of an element-bearing
/// complexType (both modeled as real C# inheritance - see TryGetInheritanceBase).
///
/// An element reference whose target has substitution group members (e.g. an abstract "Frame" head
/// with concrete substitutes like ResourceFrame/ServiceFrame/TimetableFrame) is treated exactly like
/// an implicit xsd:choice over [the head itself, if not abstract] + [every transitive substitute] -
/// this is what lets real instance documents (which use the concrete substitutes, never the abstract
/// head) round-trip correctly.
///
/// Explicitly out of scope (throws <see cref="NotSupportedException"/> with a clear message
/// rather than silently generating something wrong): xsd:any wildcards, and repeating/optional
/// nested groups that aren't themselves xsd:choice (a bare minOccurs/maxOccurs != 1 on a nested
/// xsd:sequence/xsd:all with more than one member has no wrapping element, so there's no principled
/// way to know where one repetition ends and the next begins without a real parser state machine;
/// wrap the group in a named element instead).
/// </summary>
public sealed class SchemaModelBuilder
{
    private readonly XmlSchemaSet _set;
    private readonly SchemaModel _model;
    private readonly HashSet<string> _usedTypeNames = new(StringComparer.Ordinal);
    private readonly Dictionary<XmlSchemaComplexType, ClassTypeModel> _classCache = [];
    private readonly Dictionary<XmlSchemaSimpleType, EnumTypeModel> _enumCache = [];
    private readonly Dictionary<XmlSchemaSimpleType, UnionTypeModel> _simpleUnionCache = [];
    private readonly Dictionary<XmlSchemaChoice, UnionTypeModel> _choiceUnionCache = [];
    private readonly Dictionary<XmlQualifiedName, UnionTypeModel> _substitutionUnionCache = [];
    private readonly Dictionary<XmlQualifiedName, List<XmlSchemaElement>> _directSubstitutes = [];
    private readonly Dictionary<XmlQualifiedName, List<XmlSchemaElement>> _transitiveSubstitutesCache = [];
    private readonly Dictionary<string, string> _sourceUriToXmlNamespace = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _xmlNamespaceToClrNamespace = new(StringComparer.Ordinal);
    private readonly HashSet<string> _primaryXmlNamespaces;
    private readonly IReadOnlyDictionary<string, string> _namespaceOverrides;

    /// <summary>
    /// xsd:restriction classes whose own-vs-inherited element dedup must wait until every class's
    /// BaseClass chain is fully resolved - see the comment where these get added, in PopulateClass.
    /// </summary>
    private readonly List<(ClassTypeModel Model, List<MemberModel> Scratch)> _pendingRestrictionElementDedup = [];

    private SchemaModelBuilder(XmlSchemaSet set, string rootClrNamespace, IReadOnlyCollection<string> primaryXmlNamespaces,
        IReadOnlyDictionary<string, string>? namespaceOverrides)
    {
        _set = set;
        _model = new SchemaModel(rootClrNamespace);
        _primaryXmlNamespaces = new HashSet<string>(primaryXmlNamespaces, StringComparer.Ordinal);
        _namespaceOverrides = namespaceOverrides ?? new Dictionary<string, string>();
    }

    /// <summary>
    /// Builds the model for a compiled schema set.
    /// </summary>
    /// <param name="rootClrNamespace">The base C# namespace (e.g. from the CLI's -n). Types whose XSD
    /// targetNamespace is one of <paramref name="primaryXmlNamespaces"/> (or has no namespace at all)
    /// go directly here; every other distinct XSD namespace gets its own sub-namespace underneath it.</param>
    /// <param name="primaryXmlNamespaces">Target namespace(s) of the explicitly-passed root schema
    /// file(s) - see <see cref="SchemaLoader"/>'s LoadedSchema.RootNamespaces.</param>
    /// <param name="namespaceOverrides">Optional explicit XSD-namespace-URI -&gt; C#-sub-namespace-segment
    /// map for namespaces that shouldn't use the default URI-derived name (e.g. mapping
    /// "http://www.opengis.net/gml/3.2" to "Gml" instead of the derived "Gml32").</param>
    public static SchemaModel Build(XmlSchemaSet set, string rootClrNamespace, IReadOnlyCollection<string> primaryXmlNamespaces,
        IReadOnlyDictionary<string, string>? namespaceOverrides = null)
    {
        SchemaModelBuilder builder = new(set, rootClrNamespace, primaryXmlNamespaces, namespaceOverrides);
        builder.Run();
        return builder._model;
    }

    private const string XsdNamespace = "http://www.w3.org/2001/XMLSchema";

    private void Run()
    {
        // Reverse index of SourceUri -> the declaring schema's targetNamespace, so any schema object
        // (named or anonymous - SourceUri is populated for both) can be mapped to a C# namespace.
        foreach (XmlSchema schema in _set.Schemas())
        {
            if (schema.SourceUri is { Length: > 0 } uri)
                _sourceUriToXmlNamespace[uri] = schema.TargetNamespace ?? "";
        }

        // Build the substitution-group reverse index first (head -> direct members) so it's
        // available no matter what order types get built in below.
        foreach (XmlSchemaElement element in _set.GlobalElements.Values)
        {
            if (element.SubstitutionGroup.IsEmpty)
                continue;
            if (!_directSubstitutes.TryGetValue(element.SubstitutionGroup, out List<XmlSchemaElement>? list))
                _directSubstitutes[element.SubstitutionGroup] = list = [];
            list.Add(element);
        }

        // Pre-register every named complex type first so forward references (a type used before
        // it's declared in file order, or mutually-recursive types) resolve to a stable name.
        // GlobalTypes always includes the built-in xs:* types (e.g. xs:anyType, whose content model
        // is an xsd:any wildcard) - skip those, we only generate types for user-defined ones.
        foreach (XmlSchemaType type in _set.GlobalTypes.Values)
        {
            if (type is XmlSchemaComplexType ct && !ct.QualifiedName.IsEmpty && ct.QualifiedName.Namespace != XsdNamespace)
                GetOrBuildClass(ct, ct.QualifiedName.Name);
        }

        foreach (XmlSchemaType type in _set.GlobalTypes.Values)
        {
            if (type is XmlSchemaSimpleType st && !st.QualifiedName.IsEmpty && st.QualifiedName.Namespace != XsdNamespace)
                ResolveSimpleType(st, st.QualifiedName.Name);
        }

        foreach (XmlSchemaElement element in _set.GlobalElements.Values)
        {
            // Same reasoning as union cases (see IsUninstantiable): an abstract element, or one whose
            // own type is abstract, can never be a real document's root - a real instance document
            // would use one of its concrete substitutes as the root element instead. Skip it entirely
            // rather than generating a Document helper that tries "new AbstractType()".
            if (IsUninstantiable(element))
                continue;

            MemberTypeInfo typeInfo = ResolveType(element.ElementSchemaType!, element.QualifiedName.Name);
            _model.RootElements.Add(new RootElementModel(
                typeInfo.ClrTypeName, element.QualifiedName.Name,
                string.IsNullOrEmpty(element.QualifiedName.Namespace) ? null : element.QualifiedName.Namespace,
                ResolveClrNamespaceForXmlNamespace(element.QualifiedName.Namespace))
            {
                ParseMethod = typeInfo.ParseMethod,
                FormatMethod = typeInfo.FormatMethod,
            });
        }

        // Now that every class in the model is fully built (including every BaseClass link), it's
        // safe to dedup each xsd:restriction class's own-vs-inherited elements - see where these were
        // queued, in PopulateClass, for why this can't happen while classes are still being built.
        foreach ((ClassTypeModel model, List<MemberModel> scratch) in _pendingRestrictionElementDedup)
        {
            HashSet<string> inheritedNames = new(TransitiveElementXmlNames(model.BaseClass), StringComparer.Ordinal);
            model.Elements.AddRange(scratch.Where(m => !inheritedNames.Contains(m.XmlName)));

            UniquifyMemberNames(model);
        }
    }

    private ClassTypeModel GetOrBuildClass(XmlSchemaComplexType complexType, string suggestedName)
    {
        if (_classCache.TryGetValue(complexType, out ClassTypeModel? existing))
            return existing;

        // A complexType with its own top-level XSD name must always be named after that XSD name,
        // never after whichever element/usage-site happens to reach it first during the recursive
        // graph walk - Run()'s own pre-registration loop calls this with the type's real name, but a
        // member elsewhere (e.g. an element whose type="..." references this same complexType) can
        // recurse into building it first, before that loop's own turn for this exact type is reached,
        // and would otherwise "win" the cache with a merely-contextual name (e.g. "OwnerPointsInSequence"
        // instead of the type's own "PointsInSequenceRelStructure"). An anonymous (nested, no top-level
        // name) complexType has no such name to prefer, so it keeps the caller-supplied contextual one.
        string effectiveName = complexType.QualifiedName.IsEmpty ? suggestedName : complexType.QualifiedName.Name;

        string clrName = UniqueTypeName(effectiveName);
        ClassTypeModel model = new(clrName, ResolveClrNamespace(complexType)) { IsAbstract = complexType.IsAbstract };

        _classCache[complexType] = model; // register before recursing, to break reference cycles
        _model.Classes.Add(model);

        PopulateClass(model, complexType);
        return model;
    }

    private void PopulateClass(ClassTypeModel model, XmlSchemaComplexType complexType)
    {
        if (TryGetInheritanceBase(complexType, out XmlSchemaComplexType? baseComplexType, out XmlSchemaParticle? ownParticle, out bool isRestriction))
        {
            // xsd:complexContent/xsd:extension or xsd:restriction of another element-bearing
            // complexType: model as real C# inheritance instead of flattening the base's members into
            // this class too. Build the base class first (GetOrBuildClass caches, so this is a no-op
            // if already built/building).
            model.BaseClass = GetOrBuildClass(baseComplexType!, baseComplexType!.QualifiedName.Name);

            // AttributeUses is the fully-compiled/effective set either way (inherited + own), so "not
            // already a key in the base's own compiled set" is both extension's actual new attributes
            // and restriction's genuinely-narrowed ones - a same-named attribute restriction (e.g.
            // narrowing "id"'s type/required-ness) is dropped rather than re-declared, since it would
            // otherwise shadow the inherited property; the base's (wider) declaration is kept.
            foreach (XmlSchemaAttribute attr in OwnAttributes(complexType, baseComplexType))
                model.Attributes.Add(BuildAttributeMember(attr, model.ClrName));

            if (isRestriction)
            {
                // xsd:restriction must fully re-state its content (XSD has no "same as base" shorthand)
                // - NeTEx's common pattern is to flatten the *entire* inherited xsd:group-ref chain
                // straight back into the restriction, verbatim, adding nothing new. So flatten into a
                // scratch list, then (once every class's BaseClass chain is guaranteed fully resolved -
                // see _pendingRestrictionElementDedup) keep only members whose XML name isn't already
                // inherited from somewhere in the base chain - the rest is pure restatement, already
                // available via the (real C#) base class. A restriction that narrows an inherited
                // element's type or cardinality under the same name is dropped the same way attributes
                // are above: the base's original (wider) property wins rather than shadowing it.
                //
                // This can't be resolved immediately here: building this class's own new members can
                // recursively need a type that's a substitution-group member restricting a class
                // further up THIS SAME base chain (e.g. LinkSequenceGroup's own "sectionsInSequence"
                // element pulls in a concrete Section substitute while Section_VersionStructure's own
                // base link is still being assigned) - reading model.BaseClass's chain right now could
                // see it truncated mid-assembly, silently keeping members that really are duplicates.
                List<MemberModel> scratch = [];
                FlattenParticle(ownParticle, scratch, model.ClrName);
                _pendingRestrictionElementDedup.Add((model, scratch));
            }
            else
            {
                FlattenParticle(ownParticle, model.Elements, model.ClrName);
            }
        }
        else
        {
            foreach (XmlSchemaAttribute attr in CollectAttributes(complexType))
                model.Attributes.Add(BuildAttributeMember(attr, model.ClrName));

            if (complexType.ContentType == XmlSchemaContentType.TextOnly && HasSimpleContentExtension(complexType, out XmlSchemaSimpleType? simpleBase))
            {
                MemberTypeInfo typeInfo = ResolveType(simpleBase!, model.ClrName + "Value");
                model.SimpleContent = ToMember(MemberKind.Element, "Value", "", typeInfo, isOptional: false, isRepeating: false, isNillable: false);
            }
            else if (complexType.ContentType != XmlSchemaContentType.Empty)
            {
                FlattenParticle(complexType.ContentTypeParticle, model.Elements, model.ClrName);

                // mixed="true": besides the declared element children just flattened above, the
                // element itself may carry free-floating character data directly (see IsMixed's doc
                // comment) - captured separately since it has no element name of its own to key off.
                model.IsMixed = complexType.ContentType == XmlSchemaContentType.Mixed;
            }
        }

        UniquifyMemberNames(model);
    }

    /// <summary>
    /// True when <paramref name="complexType"/> is a genuine xsd:complexContent extension or
    /// restriction of another user-defined (non-xs:anyType) complexType - both modeled as C#
    /// inheritance (see PopulateClass for how their own-member sets differ). Out: the base
    /// complexType, this type's own uncompiled Particle (xsd:extension/restriction's declared
    /// content, NOT the compiled ContentTypeParticle, which would already have the base's content
    /// merged in), and whether it's a restriction (vs an extension). False for simpleContent
    /// extension, handled separately and kept flattened - see HasSimpleContentExtension.
    /// </summary>
    private static bool TryGetInheritanceBase(XmlSchemaComplexType complexType, out XmlSchemaComplexType? baseComplexType, out XmlSchemaParticle? ownParticle, out bool isRestriction)
    {
        baseComplexType = null;
        ownParticle = null;
        isRestriction = false;

        switch (complexType.ContentModel)
        {
            case XmlSchemaComplexContent { Content: XmlSchemaComplexContentExtension ext }:
                ownParticle = ext.Particle;
                break;
            case XmlSchemaComplexContent { Content: XmlSchemaComplexContentRestriction restr }:
                ownParticle = restr.Particle;
                isRestriction = true;
                break;
            default:
                return false;
        }

        if (complexType.BaseXmlSchemaType is not XmlSchemaComplexType baseCt || baseCt.QualifiedName.Namespace == XsdNamespace)
        {
            ownParticle = null;
            isRestriction = false;
            return false;
        }

        baseComplexType = baseCt;
        return true;
    }

    /// <summary>Every element XML name owned (not just inherited) by any class in the chain, from <paramref name="model"/> up through its base(s).</summary>
    private static IEnumerable<string> TransitiveElementXmlNames(ClassTypeModel? model)
    {
        for (ClassTypeModel? c = model; c is not null; c = c.BaseClass)
            foreach (MemberModel e in c.Elements)
                yield return e.XmlName;
    }

    /// <summary>
    /// The attributes <paramref name="complexType"/> adds beyond what <paramref name="baseComplexType"/>
    /// already has (base's own resolve independently via its own GetOrBuildClass/PopulateClass call) -
    /// AttributeUses is already the fully-inherited/compiled set, so the delta is just "not already a
    /// key in the base's own compiled set".
    /// </summary>
    private static IEnumerable<XmlSchemaAttribute> OwnAttributes(XmlSchemaComplexType complexType, XmlSchemaComplexType baseComplexType) =>
        complexType.AttributeUses.Values
            .OfType<XmlSchemaAttribute>()
            .Where(a => a.Use != XmlSchemaUse.Prohibited && baseComplexType.AttributeUses[a.QualifiedName] is null);

    /// <summary>
    /// Property names are only derived from XML names within their own member list, so an attribute
    /// and an element (or two elements) can sanitize to the same C# identifier (e.g. "status" and
    /// "Status"). Dedup within the class after the fact rather than trying to avoid it during naming,
    /// since collisions span attributes/elements/simpleContent and are rare enough not to warrant
    /// threading a shared name set through every builder path.
    /// </summary>
    private static void UniquifyMemberNames(ClassTypeModel model)
    {
        // Seeded with the class's own name too: a member can't share its enclosing type's name in C#
        // (CS0542) - real schemas do this, e.g. a complexType named "TransportMode" with its own child
        // element also named "TransportMode". Uniquify then suffixes it the same way it already
        // handles any other member-vs-member collision.
        HashSet<string> used = new(StringComparer.Ordinal) { model.ClrName };
        foreach (MemberModel? m in model.Attributes.Concat(model.Elements).Append(model.SimpleContent).Where(m => m is not null))
            m!.ClrPropertyName = CSharpIdentifiers.Uniquify(m.ClrPropertyName, used);
    }

    private static bool HasSimpleContentExtension(XmlSchemaComplexType complexType, out XmlSchemaSimpleType? baseSimpleType)
    {
        // Whether declared via <xsd:simpleContent><xsd:extension base="..."> or a direct
        // restriction of a simple type, the compiled BaseXmlSchemaType is the effective text-content
        // type - except a complexContent extension can itself extend a *complex* type that has simple
        // content (chaining, e.g. ClassInFrameRefStructure -> ClassRefStructure -> xsd:string), so walk
        // up through any number of complex links to find the ultimate simple type.
        XmlSchemaType? current = complexType.BaseXmlSchemaType;
        while (current is XmlSchemaComplexType ct)
            current = ct.BaseXmlSchemaType;

        baseSimpleType = current as XmlSchemaSimpleType;
        return baseSimpleType is not null;
    }

    private IEnumerable<XmlSchemaAttribute> CollectAttributes(XmlSchemaComplexType complexType)
    {
        Dictionary<XmlQualifiedName, XmlSchemaAttribute> byName = [];

        void Merge(XmlSchemaComplexType ct)
        {
            if (ct.BaseXmlSchemaType is XmlSchemaComplexType baseCt)
                Merge(baseCt); // base first, so derived overrides on name collision

            foreach (object value in ct.AttributeUses.Values)
            {
                if (value is XmlSchemaAttribute a && a.Use != XmlSchemaUse.Prohibited)
                    byName[a.QualifiedName] = a;
            }
        }

        Merge(complexType);
        return byName.Values;
    }

    private MemberModel BuildAttributeMember(XmlSchemaAttribute attribute, string ownerContextName)
    {
        string name = attribute.QualifiedName.Name;
        MemberTypeInfo typeInfo = ResolveType(attribute.AttributeSchemaType!, CombineContext(ownerContextName, CSharpIdentifiers.ToPascalIdentifier(name)));
        bool isOptional = attribute.Use != XmlSchemaUse.Required;

        MemberModel member = ToMember(MemberKind.Attribute, name, attribute.QualifiedName.Namespace, typeInfo, isOptional, isRepeating: false, isNillable: false,
            xsdDefaultValue: attribute.DefaultValue ?? attribute.FixedValue);
        return member;
    }

    private void FlattenParticle(XmlSchemaParticle? particle, List<MemberModel> target, string ownerContextName)
    {
        switch (particle)
        {
            case null:
                return;

            case XmlSchemaElement element:
                target.Add(BuildElementMember(element, ownerContextName));
                return;

            case XmlSchemaAny:
                // No element name to key off of - captured verbatim as a list of raw XmlElement,
                // consumed as the fallback for whatever doesn't match a known member (see EmitClassReadXml).
                if (target.Any(m => m.IoKind == IoKind.Wildcard))
                    return; // a class only needs one catch-all bucket even if the schema has several <xsd:any>.
                target.Add(new MemberModel(MemberKind.Element, "AnyElements", "", "System.Xml.XmlElement", IoKind.Wildcard) { IsRepeating = true });
                return;

            // A <xsd:choice> with fewer than two branches isn't really offering an alternative at
            // all - real-world schemas (e.g. NeTEx's "*_RelStructure" types) routinely wrap a single
            // element ref in a <xsd:choice> purely to give it its own minOccurs/maxOccurs, instead of
            // using <xsd:sequence> (e.g. stopPointsInSequence_RelStructure: a <xsd:choice> with one
            // <xsd:element ref="StopPointInJourneyPattern" minOccurs="2" maxOccurs="unbounded"/>).
            // Falling through to the XmlSchemaGroupBase handling below - the same handling an
            // equivalently-shaped <xsd:sequence>/<xsd:all> already gets - means that branch's own
            // minOccurs/maxOccurs drives the resulting member directly, instead of being silently
            // dropped in favor of the choice's own (default 1..1, and thus non-repeating) occurs. A
            // real xsd:choice with 2+ genuine alternatives still becomes a union, as before.
            case XmlSchemaChoice choice when choice.Items.Count >= 2:
                target.Add(BuildChoiceMember(choice, ownerContextName));
                return;

            case XmlSchemaGroupBase group when group.MaxOccurs <= 1m:
                // A group that appears at most once has no repetition ambiguity, so it can just be
                // flattened inline. If the group itself is optional (minOccurs=0), its absence means
                // none of its members appear, so every member flattened from it becomes optional too -
                // even ones individually declared as required within the group.
                bool forceOptional = group.MinOccurs == 0m;
                foreach (XmlSchemaObject child in group.Items)
                {
                    int before = target.Count;
                    FlattenParticle((XmlSchemaParticle)child, target, ownerContextName);
                    if (forceOptional)
                        for (int i = before; i < target.Count; i++)
                            target[i].IsOptional = true;
                }
                return;

            case XmlSchemaGroupBase repeating:
            {
                // A repeating group with exactly one member particle (e.g. <sequence maxOccurs="unbounded">
                // <element ref="X"/></sequence>) is unambiguous - it's just a wordier way of writing
                // maxOccurs="unbounded" on that one member directly. Only >1 members are genuinely
                // ambiguous (no wrapping element to tell repetitions apart).
                List<MemberModel> scratch = [];
                foreach (XmlSchemaObject child in repeating.Items)
                    FlattenParticle((XmlSchemaParticle)child, scratch, ownerContextName);

                if (scratch.Count == 1)
                {
                    // List<List<T>> if it's also xsd:list-valued - see BuildElementMember.
                    scratch[0].IsRepeating = true;
                    target.Add(scratch[0]);
                    return;
                }

                // More than one member with no wrapping element: still unambiguous as long as it's
                // *repetitions* that are ambiguous, not what's inside one repetition - each repetition
                // is read the same way a multi-element xsd:choice branch is (see BuildRowClass), just
                // as a plain repeating member instead of a union case.
                string repeatingRowClrName = CSharpIdentifiers.Uniquify(CombineContext(ownerContextName, "Row"), _usedTypeNames);
                ClassTypeModel repeatingRow = new(repeatingRowClrName, ResolveClrNamespace(repeating)) { IsRow = true };

                repeatingRow.Elements.AddRange(scratch);
                _model.Classes.Add(repeatingRow);

                UniquifyMemberNames(repeatingRow);

                string repeatingFirstName = repeatingRow.Elements[0].XmlName;

                target.Add(new MemberModel(MemberKind.Element, CSharpIdentifiers.ToPascalIdentifier(repeatingFirstName),
                    repeatingFirstName, repeatingRow.ClrName, IoKind.Serializable)
                {
                    IsRepeating = true,
                    TriggerNames = RowTriggerNames(repeatingRow.Elements),
                });

                return;
            }

            case XmlSchemaGroupRef groupRef:
            {
                // Post-compile, ContentTypeParticle normally has group refs already inlined; this is a defensive fallback.
                XmlSchemaGroup resolved = FindGroup(groupRef.RefName) ?? throw new NotSupportedException(
                    $"'{ownerContextName}' {Loc(groupRef)}: could not resolve group reference '{groupRef.RefName}'.");
                // Same "the whole group is optional, so is every member it flattens into" reasoning as
                // the XmlSchemaGroupBase case above - a <group ref="X" minOccurs="0"/> whose own referenced
                // group's members are individually required (e.g. NeTEx's InterchangeEndpointGroup, whose
                // FromPointRef/ToPointRef have no minOccurs of their own) is still legitimately absent as a
                // whole, so those members must be nullable too, not just the ones already marked optional.
                bool groupRefForceOptional = groupRef.MinOccurs == 0m;
                int groupRefBefore = target.Count;
                FlattenParticle(resolved.Particle, target, ownerContextName);
                if (groupRefForceOptional)
                    for (int i = groupRefBefore; i < target.Count; i++)
                        target[i].IsOptional = true;
                return;
            }

            default:
                throw new NotSupportedException($"'{ownerContextName}' {Loc(particle)}: unsupported particle kind '{particle.GetType().Name}'.");
        }
    }

    private static string Loc(XmlSchemaObject obj) => $"(at {obj.SourceUri}:{obj.LineNumber})";

    private UnionTypeModel? FindUnion(string clrName) => _model.Unions.FirstOrDefault(u => u.ClrName == clrName);

    /// <summary>
    /// Builds a suggested name for a nested anonymous type from its owner's context name and its own
    /// local segment (e.g. a property/element name). Deeply-nested schemas (choice inside row inside
    /// choice inside ...) would otherwise accumulate one segment per level into an unreadable,
    /// hard-to-type name - past a length budget, drop the accumulated prefix and start fresh from
    /// just the local segment, relying on global uniquification to suffix a number on collision.
    /// </summary>
    private static string CombineContext(string ownerContext, string localSegment) =>
        ownerContext.Length > 40 ? localSegment : ownerContext + localSegment;

    /// <summary>
    /// PascalCases, length-caps, and globally uniquifies a suggested type name in one step - the
    /// single choke point for every class/enum/union/case name, so the length policy lives in one
    /// place. Truncation can only ever make two different suggestions collide, never a correct name
    /// incorrect, and Uniquify's numeric suffix disambiguates that same as any other collision.
    /// </summary>
    private string UniqueTypeName(string suggested)
    {
        const int maxLen = 60;
        string pascal = CSharpIdentifiers.ToPascalIdentifier(suggested);
        if (pascal.Length > maxLen)
            pascal = pascal.Substring(0, maxLen);
        return CSharpIdentifiers.Uniquify(pascal, _usedTypeNames);
    }

    /// <summary>The C# namespace a schema object's own type should be emitted into, from its declaring file's targetNamespace.</summary>
    private string ResolveClrNamespace(XmlSchemaObject obj) =>
        ResolveClrNamespaceForXmlNamespace(
            obj.SourceUri is { Length: > 0 } uri && _sourceUriToXmlNamespace.TryGetValue(uri, out string? ns) ? ns : "");

    /// <summary>
    /// Maps one XSD targetNamespace URI to a C# namespace: no namespace, or one of the root schema
    /// file(s)' own namespaces, goes straight into the root namespace; everything else (i.e. brought
    /// in via xsd:import, like GML or SIRI types inside NeTEx) gets its own sub-namespace - explicitly
    /// via --namespace-map if given, otherwise derived from the URI itself.
    /// </summary>
    private string ResolveClrNamespaceForXmlNamespace(string xmlNamespace)
    {
        if (_xmlNamespaceToClrNamespace.TryGetValue(xmlNamespace, out string? cached))
            return cached;

        string result;
        if (string.IsNullOrEmpty(xmlNamespace) || _primaryXmlNamespaces.Contains(xmlNamespace))
        {
            result = _model.RootClrNamespace;
        }
        else if (_namespaceOverrides.TryGetValue(xmlNamespace, out string? overridden))
        {
            result = string.IsNullOrEmpty(overridden) ? _model.RootClrNamespace : $"{_model.RootClrNamespace}.{overridden}";
        }
        else
        {
            result = $"{_model.RootClrNamespace}.{DeriveNamespaceSegment(xmlNamespace)}";
        }

        _xmlNamespaceToClrNamespace[xmlNamespace] = result;
        return result;
    }

    /// <summary>Turns an XSD namespace URI into a C# identifier, e.g. "http://www.opengis.net/gml/3.2" -&gt; "Gml32".</summary>
    private static string DeriveNamespaceSegment(string xmlNamespace)
    {
        string raw;
        if (Uri.TryCreate(xmlNamespace, UriKind.Absolute, out Uri? uri) && uri.Segments.Length > 0)
        {
            string[] segments = uri.AbsolutePath.Split(['/'], StringSplitOptions.RemoveEmptyEntries);
            raw = segments.Length > 0 ? string.Join("_", segments) : uri.Host;
        }
        else
        {
            raw = xmlNamespace;
        }

        string pascal = CSharpIdentifiers.ToPascalIdentifier(raw);
        return pascal.Length > 0 ? pascal : "Ns";
    }

    private List<XmlSchemaElement> GetTransitiveSubstitutes(XmlQualifiedName head)
    {
        if (_transitiveSubstitutesCache.TryGetValue(head, out List<XmlSchemaElement>? cached))
            return cached;

        List<XmlSchemaElement> result = [];
        _transitiveSubstitutesCache[head] = result; // guard against cycles before recursing
        if (_directSubstitutes.TryGetValue(head, out List<XmlSchemaElement>? direct))
        {
            foreach (XmlSchemaElement member in direct)
            {
                result.Add(member);
                result.AddRange(GetTransitiveSubstitutes(member.QualifiedName));
            }
        }
        return result;
    }

    /// <summary>
    /// If <paramref name="element"/>'s target has substitution group members, builds (or returns the
    /// cached) union over [the head itself, unless abstract] + [every transitive substitute] -
    /// otherwise returns null (the ordinary, non-substitutable case).
    /// </summary>
    private UnionTypeModel? TryGetSubstitutionUnion(XmlSchemaElement element, string contextName)
    {
        // Substitution only ever applies to a genuine <xsd:element ref="Head"/> particle (RefName
        // non-empty) - never to a particle declared locally via name="Head" that merely happens to
        // share the head's local name (and possibly type). XSD gives local declarations no
        // substitutionGroup at all; a real schema can and does have a global substitution-group head
        // and an unrelated local element of the same name in different scopes (e.g. NeTEx's global
        // "EntranceRef" head vs. a same-named but independent local element inside
        // SitePathLinkEndStructure) - without this check, GetTransitiveSubstitutes(element.QualifiedName)
        // would key purely on name+namespace and incorrectly pull in the head's whole substitute set
        // for an element that can only ever literally be itself.
        if (element.RefName.IsEmpty)
            return null;

        List<XmlSchemaElement> members = GetTransitiveSubstitutes(element.QualifiedName);
        if (members.Count == 0)
            return null;

        if (_substitutionUnionCache.TryGetValue(element.QualifiedName, out UnionTypeModel? existing))
            return existing;

        string suggested = CombineContext(contextName, CSharpIdentifiers.ToPascalIdentifier(element.QualifiedName.Name) + "Substitution");
        string clrName = UniqueTypeName(suggested);
        UnionTypeModel union = new(clrName, ResolveClrNamespace(element)) { IsElementChoice = true };
        _substitutionUnionCache[element.QualifiedName] = union;
        _model.Unions.Add(union);

        if (!IsUninstantiable(element))
            union.Cases.Add(BuildElementCase(element, clrName));
        foreach (XmlSchemaElement member in members)
            if (!IsUninstantiable(member))
                union.Cases.Add(BuildElementCase(member, clrName));

        AssignCaseWrapping(union);
        return union;
    }

    /// <summary>
    /// True when <paramref name="element"/> can never legitimately be "the value" of a union case:
    /// either it's abstract itself (a pure substitution-group connector - real instance documents
    /// always use a concrete substitute instead, never the head literally), or its own type is an
    /// abstract complexType (would need "new AbstractType()", which won't compile once abstract
    /// complexTypes are modeled as C# `abstract` classes - see ClassTypeModel.IsAbstract). Either way
    /// there's no concrete type this tool could construct for it (no xsi:type dispatch support), so
    /// the branch/substitute is skipped entirely rather than generated broken.
    /// </summary>
    private static bool IsUninstantiable(XmlSchemaElement element) =>
        element.IsAbstract || (element.ElementSchemaType is XmlSchemaComplexType ct && ct.IsAbstract);

    /// <summary>
    /// A case only needs its synthesized wrapper record when its ValueClrType collides with another
    /// case's in the *same* union (union case types must be pairwise distinct, and even when they
    /// don't collide, the wrapper is what lets Write recover which XML branch a value came from - see
    /// UnionCaseModel.IsWrapped). Anything else - the common case, e.g. a choice between two different
    /// generated classes - can use the value type directly with no wrapper at all. Run once a union's
    /// Cases list is fully populated (so the whole set is known), never before.
    /// </summary>
    private static void AssignCaseWrapping(UnionTypeModel union)
    {
        foreach (IGrouping<string, UnionCaseModel> group in union.Cases.GroupBy(c => c.ValueClrType, StringComparer.Ordinal))
        {
            if (group.Count() > 1)
                continue; // collision - every case here keeps its synthesized wrapper (the default).

            UnionCaseModel c = group.Single();

            // A token-list case's real value is List<ValueClrType>, not ValueClrType - it always needs
            // a wrapper record (whose Value ends up typed List<ValueClrType>, see EmitUnion) so the
            // union's case type is never a bare "List<SomeEnum>" directly, and so ReadXml/WriteXml can
            // rely on "the wrapper's Value is a List<T>" uniformly - see EmitElementChoiceUnion.
            if (c.IsTokenList)
                continue;

            c.IsWrapped = false;
            c.CaseClrName = c.ValueClrType;
        }
    }

    private XmlSchemaGroup? FindGroup(XmlQualifiedName name)
    {
        foreach (XmlSchema schema in _set.Schemas())
        {
            if (schema.Groups[name] is XmlSchemaGroup g)
                return g;
        }
        return null;
    }

    private MemberModel BuildElementMember(XmlSchemaElement element, string ownerContextName)
    {
        string name = element.QualifiedName.Name;
        bool isOptional = element.MinOccurs == 0m;
        bool isRepeating = element.MaxOccurs > 1m;

        UnionTypeModel? substitutionUnion = TryGetSubstitutionUnion(element, ownerContextName);
        if (substitutionUnion is not null)
        {
            // Union declarations lower to structs, so this is a value type.
            MemberTypeInfo unionTypeInfo = new(substitutionUnion.ClrName, IoKind.Serializable, true, null, null, false, false);
            return ToMember(MemberKind.Element, "Subst_" + name, "", unionTypeInfo, isOptional, isRepeating, isNillable: false);
        }

        MemberTypeInfo typeInfo = ResolveType(element.ElementSchemaType!, CombineContext(ownerContextName, CSharpIdentifiers.ToPascalIdentifier(name)));

        // Both repeating (sibling elements) and xsd:list-valued (each one's text is itself a
        // space-separated list) is fine, not ambiguous - it's just a List<List<T>>: outer = one entry
        // per sibling element, inner = that element's own space-separated tokens. See EmitProperty /
        // EmitWriteElement / EmitReadElementCaseBody for the List<List<T>> codegen.
        MemberModel member = ToMember(MemberKind.Element, name, element.QualifiedName.Namespace, typeInfo, isOptional, isRepeating, element.IsNillable,
            xsdDefaultValue: element.DefaultValue ?? element.FixedValue);
        return member;
    }

    private MemberModel BuildChoiceMember(XmlSchemaChoice choice, string ownerContextName)
    {
        UnionTypeModel union = GetOrBuildChoiceUnion(choice, ownerContextName);

        // Union declarations lower to structs, so this is a value type.
        MemberTypeInfo typeInfo = new(union.ClrName, IoKind.Serializable, true, null, null, false, false);

        // A choice is skippable in practice - and so must be its C# member, and, per RowTriggerNames,
        // so must any row/repeating-group member built from it - not just when it's explicitly
        // minOccurs="0" itself, but also when every one of its own branches is (NeTEx's
        // ModeValidityParametersGroup wraps VehicleModes/TransportModes, both individually
        // minOccurs="0", in a choice with no minOccurs of its own - XSD content-model validation
        // still accepts zero elements there, since "choose branch X" can itself mean "occurs zero
        // times"). Every branch kind (element, group ref, nested choice/sequence) is some
        // XmlSchemaParticle with its own MinOccurs, so this covers all of them uniformly.
        bool isOptional = choice.MinOccurs == 0m || choice.Items.OfType<XmlSchemaParticle>().All(p => p.MinOccurs == 0m);

        // A nested <choice> branch is flattened into this same union's cases regardless of its own
        // occurs (see GetOrBuildChoiceUnion) - if any such branch can repeat, this member must be a
        // list even though the outer choice itself doesn't repeat. Cardinality (e.g. "at least 2") is
        // not enforced on read, consistent with the rest of this generator.
        bool isRepeating = choice.MaxOccurs > 1m || HasRepeatingNestedChoice(choice);

        // "Choice_" is a sentinel other code checks for (e.g. "does a row start with a nested
        // choice?") - the rest is just for readability, so anchor on the first case only (same
        // reasoning as the union's own name in GetOrBuildChoiceUnion: joining every case, e.g. for a
        // substitution-group-derived choice, would blow up not just this name but every nested
        // context that gets built from it).
        //
        // Deliberately read straight from choice.Items here, NOT union.Cases: this member can be
        // built while GetOrBuildChoiceUnion(choice, ...) is still populating that very union's Cases
        // for a *different*, outer caller (e.g. EntityInVersionStructure's own build reaching this
        // exact <xsd:choice> object reentrantly through a restriction like DayType's, which
        // independently re-flattens the same group) - reading union.Cases here would then see it
        // still empty and silently fall back to the generic "Choice" name. choice.Items carries the
        // same first-branch information but is fully available immediately, with no such ordering
        // dependency.
        string? firstCaseName = choice.Items.OfType<XmlSchemaElement>().Select(e => e.QualifiedName.Name).FirstOrDefault();
        string xmlName = "Choice_" + (firstCaseName ?? "Choice");

        return ToMember(MemberKind.Element, xmlName, "", typeInfo, isOptional, isRepeating, isNillable: false);
    }

    private static bool HasRepeatingNestedChoice(XmlSchemaChoice choice) =>
        choice.Items.OfType<XmlSchemaChoice>().Any(c => c.MaxOccurs > 1m || HasRepeatingNestedChoice(c));

    private UnionTypeModel GetOrBuildChoiceUnion(XmlSchemaChoice choice, string ownerContextName)
    {
        if (_choiceUnionCache.TryGetValue(choice, out UnionTypeModel? existing))
            return existing;

        // Joining every branch name (there can be dozens, e.g. a substitution-group-derived choice)
        // makes for an unreadable, ever-growing name - anchor on just the first branch instead and
        // let the (usually already-unique) owner context plus Uniquify's numeric suffix disambiguate
        // from any sibling choice that happens to share the same first branch name.
        string? firstBranchName = choice.Items.OfType<XmlSchemaElement>().Select(e => e.QualifiedName.Name).FirstOrDefault();
        string namePart = firstBranchName ?? "Choice";
        string suggested = CombineContext(ownerContextName, CSharpIdentifiers.ToPascalIdentifier(namePart) + "Choice");
        string clrName = UniqueTypeName(suggested);
        UnionTypeModel union = new(clrName, ResolveClrNamespace(choice)) { IsElementChoice = true };

        _choiceUnionCache[choice] = union;
        _model.Unions.Add(union);

        void ProcessItem(XmlSchemaObject item)
        {
            switch (item)
            {
                case XmlSchemaElement branchElement:
                {
                    // A branch that references a substitution group head (ref=, not a local name=
                    // declaration that merely happens to share the head's name - see TryGetSubstitutionUnion for
                    // why that distinction matters) expands to a case per substitute too - same idea as
                    // flattening a nested <choice>. Abstract elements and elements whose own type is
                    // abstract are skipped (see IsUninstantiable) - they can never appear as themselves
                    // in a real instance document / can't be constructed, so they'd only ever generate
                    // a broken "new AbstractType()" case.
                    List<XmlSchemaElement> substitutes = branchElement.RefName.IsEmpty ? [] : GetTransitiveSubstitutes(branchElement.QualifiedName);
                    if (substitutes.Count == 0)
                    {
                        if (!IsUninstantiable(branchElement))
                            union.Cases.Add(BuildElementCase(branchElement, clrName));
                    }
                    else
                    {
                        if (!IsUninstantiable(branchElement))
                            union.Cases.Add(BuildElementCase(branchElement, clrName));
                        foreach (XmlSchemaElement substitute in substitutes)
                            if (!IsUninstantiable(substitute))
                                union.Cases.Add(BuildElementCase(substitute, clrName));
                    }
                    break;
                }

                case XmlSchemaGroupBase branchGroup when branchGroup.MinOccurs == 1m && branchGroup.MaxOccurs == 1m:
                    union.Cases.Add(BuildGroupCase(branchGroup, clrName));
                    break;

                // A <choice> directly nested in another <choice> is just a flatter way of writing more
                // alternatives of the same choice - standard XSD equivalence. If the nested choice
                // repeats, the containing member becomes a list (see BuildChoiceMember); cardinality
                // ("at least N") isn't enforced on read.
                case XmlSchemaChoice nestedChoice:
                    foreach (XmlSchemaObject nestedItem in nestedChoice.Items)
                        ProcessItem(nestedItem);
                    break;

                // Post-compile, ContentTypeParticle normally has group refs already inlined - this
                // only matters when this choice came from an xsd:extension's own (uncompiled) Particle
                // (see TryGetInheritanceBase), which can still contain a raw <xsd:group ref="...">
                // branch. Same defensive resolution as FlattenParticle's own XmlSchemaGroupRef case.
                case XmlSchemaGroupRef groupRef:
                {
                    XmlSchemaGroup resolvedGroup = FindGroup(groupRef.RefName) ?? throw new NotSupportedException(
                        $"'{clrName}' {Loc(groupRef)}: could not resolve group reference '{groupRef.RefName}'.");
                    if (resolvedGroup.Particle is XmlSchemaChoice groupChoice)
                    {
                        foreach (XmlSchemaObject nestedItem in groupChoice.Items)
                            ProcessItem(nestedItem);
                    }
                    else if (resolvedGroup.Particle is XmlSchemaGroupBase groupBase && groupRef.MinOccurs == 1m && groupRef.MaxOccurs == 1m)
                    {
                        union.Cases.Add(BuildGroupCase(groupBase, clrName));
                    }
                    else
                    {
                        throw new NotSupportedException(
                            $"'{clrName}' {Loc(groupRef)}: xsd:choice branch referencing group '{groupRef.RefName}' is not " +
                            "supported (its content isn't a simple non-repeating group or a nested xsd:choice).");
                    }
                    break;
                }

                default:
                    throw new NotSupportedException(
                        $"'{clrName}' {Loc(item)}: xsd:choice branch of kind '{item.GetType().Name}' is not supported. " +
                        "Element branches, simple (non-repeating) group branches, and trivially-nested xsd:choice " +
                        "branches are; a branch that is itself xsd:any or a repeating/optional group has no fixed " +
                        "wrapping shape to key off of. Wrap the branch in a named element instead.");
            }
        }

        foreach (XmlSchemaObject item in choice.Items)
            ProcessItem(item);

        AssignCaseWrapping(union);
        return union;
    }

    private UnionCaseModel BuildElementCase(XmlSchemaElement branchElement, string unionClrName)
    {
        string name = branchElement.QualifiedName.Name;
        MemberTypeInfo typeInfo = ResolveType(branchElement.ElementSchemaType!, CombineContext(unionClrName, CSharpIdentifiers.ToPascalIdentifier(name)));

        // Case wrapper records are emitted at the top level of the namespace, so their names must be
        // globally unique, not just unique within this one union's cases.
        string caseName = UniqueTypeName(name + "Case");

        UnionCaseModel caseModel = new(caseName, typeInfo.ClrTypeName, typeInfo.IoKind)
        {
            ElementXmlName = name,
            ElementXmlNamespace = string.IsNullOrEmpty(branchElement.QualifiedName.Namespace) ? null : branchElement.QualifiedName.Namespace,
            TriggerNames = [name],
        };

        ApplyTypeInfoToCase(caseModel, typeInfo);
        return caseModel;
    }

    /// <summary>
    /// Builds a "row" type for a &lt;sequence&gt;/&lt;all&gt; group that has no wrapping element of its
    /// own (a multi-element xsd:choice branch, or a repeating group with more than one member): a
    /// class with bare ReadFrom/WriteTo instead of IXmlSerializable's ReadXml/WriteXml, dispatched on
    /// by its leading element's trigger name(s) (unique among siblings by XSD's determinism rules).
    /// </summary>
    private (ClassTypeModel Row, string FirstXmlName, List<string> TriggerNames) BuildRowClass(XmlSchemaGroupBase group, string contextName)
    {
        string rowClrName = CSharpIdentifiers.Uniquify(CombineContext(contextName, "Row"), _usedTypeNames);
        ClassTypeModel rowModel = new(rowClrName, ResolveClrNamespace(group)) { IsRow = true };

        _model.Classes.Add(rowModel);

        FlattenParticle(group, rowModel.Elements, rowClrName);
        UniquifyMemberNames(rowModel);

        if (rowModel.Elements.Count == 0)
            throw new NotSupportedException($"'{contextName}' {Loc(group)}: a group with no wrapping element has no elements to key off of.");

        if (rowModel.Elements.Any(m => m.IoKind == IoKind.Wildcard))
            throw new NotSupportedException($"'{contextName}' {Loc(group)}: xsd:any inside a group with no wrapping element is not supported.");

        string firstName = rowModel.Elements[0].XmlName;
        return (rowModel, firstName, RowTriggerNames(rowModel.Elements));
    }

    /// <summary>
    /// The full set of XML element names that can legally be the very first thing encountered when
    /// reading one repetition/branch of a "row" (2+ members with no wrapping element of their own -
    /// see BuildRowClass and the XmlSchemaGroupBase "repeating" case in FlattenParticle): normally
    /// just the row's own first member's name, but if that member is itself optional (e.g. NeTEx's
    /// ScopingValidityParametersGroup chains several individually-optional sub-groups - a real
    /// instance routinely starts with the second or third one, omitting the first entirely), a real
    /// instance can legitimately skip straight past it - so the *next* member's trigger names must be
    /// included too, and so on through each further consecutive optional member, stopping at (and
    /// including) the first member that's actually required, since nothing after a required member
    /// can ever be where a valid instance starts. A member that's itself a nested choice contributes
    /// every one of its own cases' trigger names, the same way a plain member contributes its own.
    /// </summary>
    private List<string> RowTriggerNames(List<MemberModel> elements)
    {
        List<string> result = [];
        foreach (MemberModel member in elements)
        {
            result.AddRange(FindUnion(member.ClrTypeName) is { IsElementChoice: true } union
                ? union.Cases.SelectMany(c => c.TriggerNames)
                : member.TriggerNames);
            if (!member.IsOptional)
                break;
        }
        return result;
    }

    private UnionCaseModel BuildGroupCase(XmlSchemaGroupBase branchGroup, string unionClrName)
    {
        (ClassTypeModel row, string firstName, List<string> triggerNames) = BuildRowClass(branchGroup, unionClrName);
        string caseName = UniqueTypeName(firstName + "Case");

        return new UnionCaseModel(caseName, row.ClrName, IoKind.Serializable)
        {
            ElementXmlName = firstName,
            TriggerNames = triggerNames,
            ElementXmlNamespace = null,
        };
    }

    private static void ApplyTypeInfoToCase(UnionCaseModel caseModel, MemberTypeInfo typeInfo)
    {
        caseModel.ValueIsValueType = typeInfo.IsValueType;
        caseModel.ParseMethod = typeInfo.ParseMethod;
        caseModel.FormatMethod = typeInfo.FormatMethod;
        caseModel.IsTokenList = typeInfo.IsList;
    }

    private MemberTypeInfo ResolveSimpleType(XmlSchemaSimpleType simpleType, string contextName)
    {
        if (simpleType.Content is XmlSchemaSimpleTypeList list)
        {
            // .ItemType is only populated for an inline anonymous item type; a reference to a named
            // item type (the common case) only resolves through .BaseItemType.
            XmlSchemaSimpleType itemType = list.ItemType ?? list.BaseItemType
                ?? throw new NotSupportedException($"'{contextName}': xsd:list item type could not be resolved.");
            MemberTypeInfo itemInfo = ResolveType(itemType, contextName);

            return itemInfo with { IsList = true };
        }

        if (simpleType.Content is XmlSchemaSimpleTypeUnion union)
        {
            UnionTypeModel unionModel = GetOrBuildSimpleUnion(simpleType, contextName, union);

            // Union declarations lower to structs, so this is a value type.
            return new MemberTypeInfo(unionModel.ClrName, IoKind.Serializable, true, null, null, false, false);
        }

        List<XmlSchemaEnumerationFacet> facets = CollectEnumerationFacets(simpleType);
        if (facets.Count > 0)
        {
            bool isInherentList = simpleType.Datatype?.Variety == XmlSchemaDatatypeVariety.List;
            EnumTypeModel enumModel = GetOrBuildEnum(simpleType, contextName, isInherentList, facets);

            return new MemberTypeInfo(enumModel.ClrName, IoKind.Enum, true, null, null, false, isInherentList);
        }

        if (XsdBuiltInTypes.TryGet(simpleType.TypeCode, out BuiltInTypeInfo? builtin))
        {
            return new MemberTypeInfo(builtin.ClrType, IoKind.Primitive, builtin.IsValueType, builtin.ParseMethod, builtin.FormatMethod,
                simpleType.TypeCode == XmlTypeCode.Base64Binary, false);
        }

        return new MemberTypeInfo("string", IoKind.Primitive, false, null, null, false, false);
    }

    private MemberTypeInfo ResolveType(XmlSchemaType type, string contextName) => type switch
    {
        XmlSchemaComplexType ct => new MemberTypeInfo(GetOrBuildClass(ct, contextName).ClrName, IoKind.Serializable, false, null, null, false, false),
        XmlSchemaSimpleType st => ResolveSimpleType(st, contextName),
        _ => new MemberTypeInfo("string", IoKind.Primitive, false, null, null, false, false),
    };

    private static List<XmlSchemaEnumerationFacet> CollectEnumerationFacets(XmlSchemaSimpleType simpleType)
    {
        XmlSchemaSimpleType? current = simpleType;
        while (current is not null)
        {
            if (current.Content is XmlSchemaSimpleTypeRestriction restriction)
            {
                List<XmlSchemaEnumerationFacet> facets = [.. restriction.Facets.OfType<XmlSchemaEnumerationFacet>()];
                if (facets.Count > 0)
                    return facets;

                current = restriction.BaseType ?? current.BaseXmlSchemaType as XmlSchemaSimpleType;
                continue;
            }

            if (current.Content is XmlSchemaSimpleTypeList listContent)
            {
                // xsd:list of an enumeration (e.g. NMTOKENS with enumeration on the item type).
                if ((listContent.ItemType ?? listContent.BaseItemType) is { } itemType)
                    return CollectEnumerationFacets(itemType);
            }

            break;
        }
        return [];
    }

    private EnumTypeModel GetOrBuildEnum(XmlSchemaSimpleType simpleType, string suggestedName, bool isList, List<XmlSchemaEnumerationFacet> facets)
    {
        if (_enumCache.TryGetValue(simpleType, out EnumTypeModel? existing))
            return existing;

        string clrName = UniqueTypeName(suggestedName);
        // NMTOKEN/token/NMTOKENS all have XSD's "collapse" whitespace facet (strip leading/trailing,
        // collapse internal runs, before validating) - unlike plain string/normalizedString ("preserve"
        // /"replace"). Real-world NeTEx data relies on this: e.g. a PathHeadingEnumeration (NMTOKEN)
        // value of "left " or " forward" is spec-legal and must parse, not just the exact-trimmed form.
        bool hasCollapseWhitespace = simpleType.Datatype?.TypeCode is XmlTypeCode.NmToken or XmlTypeCode.Token;

        EnumTypeModel model = new(clrName, ResolveClrNamespace(simpleType)) { IsList = isList, HasCollapseWhitespaceFacet = hasCollapseWhitespace };

        _enumCache[simpleType] = model;
        _model.Enums.Add(model);

        HashSet<string> usedMemberNames = new(StringComparer.Ordinal);
        HashSet<string> usedXmlValues = new(StringComparer.Ordinal);

        foreach (XmlSchemaEnumerationFacet facet in facets)
        {
            string xmlValue = facet.Value ?? "";

            // The same literal value can appear twice (duplicate <xsd:enumeration> facets, or an
            // inheritance chain that re-lists a base's values) - without this, each occurrence would
            // get its own uniquified member name but the same underlying XmlValue, producing two enum
            // members that map to the same string and duplicate case labels in the generated Parse switch.
            if (!usedXmlValues.Add(xmlValue))
                continue;

            string memberName = CSharpIdentifiers.Uniquify(CSharpIdentifiers.ToEnumMemberIdentifier(xmlValue), usedMemberNames);

            model.Members.Add(new EnumMemberModel(memberName, xmlValue));
        }

        return model;
    }

    private UnionTypeModel GetOrBuildSimpleUnion(XmlSchemaSimpleType simpleType, string suggestedName, XmlSchemaSimpleTypeUnion union)
    {
        if (_simpleUnionCache.TryGetValue(simpleType, out UnionTypeModel? existing))
            return existing;

        string clrName = UniqueTypeName(suggestedName);
        UnionTypeModel model = new(clrName, ResolveClrNamespace(simpleType)) { IsElementChoice = false };

        _simpleUnionCache[simpleType] = model;
        _model.Unions.Add(model);

        XmlSchemaSimpleType[] memberTypes = union.BaseMemberTypes ?? [];
        foreach (XmlSchemaSimpleType memberType in memberTypes)
        {
            string memberContext = CombineContext(clrName, CSharpIdentifiers.ToPascalIdentifier(memberType.QualifiedName.Name));
            MemberTypeInfo typeInfo = ResolveType(memberType, memberContext);

            string caseName = UniqueTypeName(memberType.QualifiedName.Name + "Case");
            UnionCaseModel caseModel = new(caseName, typeInfo.ClrTypeName, typeInfo.IoKind);

            ApplyTypeInfoToCase(caseModel, typeInfo);
            model.Cases.Add(caseModel);
        }

        AssignCaseWrapping(model);
        return model;
    }

    private static MemberModel ToMember(MemberKind kind, string xmlName, string xmlNamespace, MemberTypeInfo typeInfo,
        bool isOptional, bool isRepeating, bool isNillable, string? xsdDefaultValue = null)
    {
        string clrPropertyName = CSharpIdentifiers.ToPascalIdentifier(xmlName);

        MemberModel member = new(kind, clrPropertyName, xmlName, typeInfo.ClrTypeName, typeInfo.IoKind)
        {
            XmlNamespace = string.IsNullOrEmpty(xmlNamespace) ? null : xmlNamespace,
            IsValueType = typeInfo.IsValueType,
            IsOptional = isOptional,
            IsRepeating = isRepeating,
            IsTokenList = typeInfo.IsList,
            IsNillable = isNillable,
            ParseMethod = typeInfo.ParseMethod,
            FormatMethod = typeInfo.FormatMethod,
            IsBase64 = typeInfo.IsBase64,
            XsdDefaultValue = xsdDefaultValue,
        };
        return member;
    }

    private sealed record MemberTypeInfo(
        string ClrTypeName,
        IoKind IoKind,
        bool IsValueType,
        string? ParseMethod,
        string? FormatMethod,
        bool IsBase64,
        bool IsList = false);
}
