// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;

namespace System.Text.Json.Serialization.Metadata
{
    /// <summary>
    /// Provides JSON serialization-related metadata about a type.
    /// </summary>
    [DebuggerDisplay("{DebuggerDisplay,nq}")]
    public abstract partial class JsonTypeInfo
    {
        internal const string JsonObjectTypeName = "System.Text.Json.Nodes.JsonObject";

        internal delegate T ParameterizedConstructorDelegate<T, TArg0, TArg1, TArg2, TArg3>(TArg0 arg0, TArg1 arg1, TArg2 arg2, TArg3 arg3);

        private JsonPropertyInfoDictionaryValueList? _properties;

        /// <summary>
        /// Object constructor. If set to null type is not deserializable.
        /// </summary>
        public Func<object>? CreateObject
        {
            get => _createObject;
            set
            {
                SetCreateObject(value);
            }
        }

        private protected abstract void SetCreateObject(Delegate? createObject);
        private protected Func<object>? _createObject;

        internal Func<object>? CreateObjectForExtensionDataProperty { get; private protected set; }

        /// <summary>
        /// Gets JsonPropertyInfo list. Only applicable when Kind is Object.
        /// </summary>
        public IList<JsonPropertyInfo> Properties
        {
            get
            {
                if (_properties != null)
                {
                    return _properties;
                }

                if (Kind == JsonTypeInfoKind.Object)
                {
                    // We need to ensure SourceGen had a chance to add properties
                    LateAddProperties();
                }

                PropertyCache ??= CreatePropertyCache(capacity: 0);

                bool isReadOnly = _isConfigured || Kind != JsonTypeInfoKind.Object;
                _properties = new JsonPropertyInfoDictionaryValueList(PropertyCache, this, isReadOnly);

                return _properties;
            }
        }

        internal object? CreateObjectWithArgs { get; set; }

        // Add method delegate for non-generic Stack and Queue; and types that derive from them.
        internal object? AddMethodDelegate { get; set; }

        internal JsonPropertyInfo? DataExtensionProperty { get; set; }

        internal PolymorphicTypeResolver? PolymorphicTypeResolver { get; private set; }

        // If enumerable or dictionary, the JsonTypeInfo for the element type.
        private JsonTypeInfo? _elementTypeInfo;

        // Avoids having to perform an expensive cast to JsonTypeInfo<T> to check if there is a Serialize method.
        internal bool HasSerialize { get; set; }

        // Configure would normally have thrown why initializing properties for source gen but type had SerializeHandler
        // so it is allowed to be used for serialization but it will throw if used for deserialization
        internal bool ThrowOnDeserialize { get; set; }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void ValidateCanBeUsedForDeserialization()
        {
            if (ThrowOnDeserialize)
            {
                ThrowHelper.ThrowInvalidOperationException_NoMetadataForTypeProperties(Options.SerializerContext, Type);
            }
        }

        /// <summary>
        /// Return the JsonTypeInfo for the element type, or null if the type is not an enumerable or dictionary.
        /// </summary>
        /// <remarks>
        /// This should not be called during warm-up (initial creation of JsonTypeInfos) to avoid recursive behavior
        /// which could result in a StackOverflowException.
        /// </remarks>
        internal JsonTypeInfo? ElementTypeInfo
        {
            get
            {
                if (_elementTypeInfo == null)
                {
                    if (ElementType != null)
                    {
                        // GetOrAddJsonTypeInfo already ensures JsonTypeInfo is configured
                        // also see comment on JsonPropertyInfo.JsonTypeInfo
                        _elementTypeInfo = Options.GetOrAddJsonTypeInfo(ElementType);
                    }
                }
                else
                {
                    _elementTypeInfo.EnsureConfigured();
                }

                return _elementTypeInfo;
            }
            set
            {
                // Set by JsonMetadataServices.
                Debug.Assert(_elementTypeInfo == null);
                _elementTypeInfo = value;
            }
        }

        internal Type? ElementType { get; set; }

        // If dictionary, the JsonTypeInfo for the key type.
        private JsonTypeInfo? _keyTypeInfo;

        /// <summary>
        /// Return the JsonTypeInfo for the key type, or null if the type is not a dictionary.
        /// </summary>
        /// <remarks>
        /// This should not be called during warm-up (initial creation of JsonTypeInfos) to avoid recursive behavior
        /// which could result in a StackOverflowException.
        /// </remarks>
        internal JsonTypeInfo? KeyTypeInfo
        {
            get
            {
                if (_keyTypeInfo == null)
                {
                    if (KeyType != null)
                    {
                        Debug.Assert(PropertyInfoForTypeInfo.ConverterStrategy == ConverterStrategy.Dictionary);

                        // GetOrAddJsonTypeInfo already ensures JsonTypeInfo is configured
                        // also see comment on JsonPropertyInfo.JsonTypeInfo
                        _keyTypeInfo = Options.GetOrAddJsonTypeInfo(KeyType);
                    }
                }
                else
                {
                    _keyTypeInfo.EnsureConfigured();
                }

                return _keyTypeInfo;
            }
            set
            {
                // Set by JsonMetadataServices.
                Debug.Assert(_keyTypeInfo == null);
                _keyTypeInfo = value;
            }
        }

        internal Type? KeyType { get; set; }

        /// <summary>
        /// Options associated with JsonTypeInfo
        /// </summary>
        public JsonSerializerOptions Options { get; private set; }

        /// <summary>
        /// Type associated with JsonTypeInfo
        /// </summary>
        public Type Type { get; private set; }

        /// <summary>
        /// Converter associated with the type for the given options instance
        /// </summary>
        public JsonConverter Converter
            // For JsonTypeInfo CustomConverter is always null
            // while NonCustomConverter always contains final converter.
            // This property can be used before JsonTypeInfo is configured (especially in SourceGen case)
            // therefore it's safer to return NonCustomConverter rather than EffectiveConverter.
            => PropertyInfoForTypeInfo.DefaultConverterForType!;

        /// <summary>
        /// Determines the kind of contract metadata current JsonTypeInfo instance is customizing
        /// </summary>
        public JsonTypeInfoKind Kind { get; private set; }

        /// <summary>
        /// The JsonPropertyInfo for this JsonTypeInfo. It is used to obtain the converter for the TypeInfo.
        /// </summary>
        /// <remarks>
        /// The returned JsonPropertyInfo does not represent a real property; instead it represents either:
        /// a collection element type,
        /// a generic type parameter,
        /// a property type (if pushed to a new stack frame),
        /// or the root type passed into the root serialization APIs.
        /// For example, for a property returning <see cref="Collections.Generic.List{T}"/> where T is a string,
        /// a JsonTypeInfo will be created with .Type=typeof(string) and .PropertyInfoForTypeInfo=JsonPropertyInfo{string}.
        /// Without this property, a "Converter" property would need to be added to JsonTypeInfo and there would be several more
        /// `if` statements to obtain the converter from either the actual JsonPropertyInfo (for a real property) or from the
        /// TypeInfo (for the cases mentioned above). In addition, methods that have a JsonPropertyInfo argument would also likely
        /// need to add an argument for JsonTypeInfo.
        /// </remarks>
        internal JsonPropertyInfo PropertyInfoForTypeInfo { get; private set; }

        /// <summary>
        /// Returns a helper class used for computing the default value.
        /// </summary>
        internal DefaultValueHolder DefaultValueHolder => _defaultValueHolder ??= DefaultValueHolder.CreateHolder(Type);
        private DefaultValueHolder? _defaultValueHolder;

        /// <summary>
        /// Type specific value overriding JsonSerializerOptions NumberHandling. For DefaultJsonTypeInfoResolver it is equivalent to JsonNumberHandlingAttribute value.
        /// </summary>
        public JsonNumberHandling? NumberHandling
        {
            get => _numberHandling;
            set
            {
                CheckMutable();
                _numberHandling = value;
            }
        }

        private JsonNumberHandling? _numberHandling;

        internal JsonTypeInfo(Type type, JsonConverter converter, JsonSerializerOptions options)
        {
            Type = type;
            Options = options;
            PropertyInfoForTypeInfo = CreatePropertyInfoForTypeInfo(Type, converter, Options, this);
            ElementType = converter.ElementType;
            ConfigurePolymorphism(converter, options);

            switch (PropertyInfoForTypeInfo.ConverterStrategy)
            {
                case ConverterStrategy.Dictionary:
                    {
                        KeyType = converter.KeyType;
                    }
                    break;
                case ConverterStrategy.Object:
                case ConverterStrategy.Enumerable:
                case ConverterStrategy.Value:
                    break;
                case ConverterStrategy.None:
                    {
                        ThrowHelper.ThrowNotSupportedException_SerializationNotSupported(type);
                    }
                    break;
                default:
                    Debug.Fail($"Unexpected class type: {PropertyInfoForTypeInfo.ConverterStrategy}");
                    throw new InvalidOperationException();
            }

            Kind = GetTypeInfoKind(type, PropertyInfoForTypeInfo.ConverterStrategy);
        }

        private protected void CheckMutable()
        {
            if (_isConfigured)
            {
                ThrowHelper.ThrowInvalidOperationException_TypeInfoImmutable();
            }
        }

        private protected volatile bool _isConfigured;
        private readonly object _configureLock = new object();

        internal void EnsureConfigured()
        {
            if (_isConfigured)
                return;

            lock (_configureLock)
            {
                if (_isConfigured)
                    return;

                Configure();

                _isConfigured = true;
            }
        }

        internal virtual void Configure()
        {
            Debug.Assert(Monitor.IsEntered(_configureLock), "Configure called directly, use EnsureConfigured which locks this method");

            if (!Options.IsInitializedForMetadataGeneration)
            {
                Options.InitializeForMetadataGeneration();
            }

            PropertyInfoForTypeInfo.EnsureChildOf(this);
            PropertyInfoForTypeInfo.EnsureConfigured();

            JsonConverter converter = Converter;
            Debug.Assert(PropertyInfoForTypeInfo.ConverterStrategy == Converter.ConverterStrategy,
                $"ConverterStrategy from PropertyInfoForTypeInfo.ConverterStrategy ({PropertyInfoForTypeInfo.ConverterStrategy}) does not match converter's ({Converter.ConverterStrategy})");

            converter.ConfigureJsonTypeInfo(this, Options);

            if (_properties != null)
            {
                // If user tried to access Properties for something else than JsonTypeInfoKind.Object
                // Properties will already be read-only
                if (!_properties.IsReadOnly)
                {
                    _properties.FinishEditingAndMakeReadOnly(Type);
                }
            }
            else
            {
                // Resolver didn't modify properties

                // Source gen currently when initializes properties
                // also assigns JsonPropertyInfo's JsonTypeInfo which causes SO if there are any
                // cycles in the object graph. For that reason properties cannot be added immediately.
                // This is a no-op for ReflectionJsonTypeInfo
                LateAddProperties();
            }

            if (DataExtensionProperty != null)
            {
                DataExtensionProperty.EnsureChildOf(this);
                DataExtensionProperty.EnsureConfigured();
            }

            if (converter.ConverterStrategy == ConverterStrategy.Object)
            {
                PropertyCache ??= CreatePropertyCache(capacity: 0);

                foreach (var jsonPropertyInfoKv in PropertyCache.List)
                {
                    JsonPropertyInfo jsonPropertyInfo = jsonPropertyInfoKv.Value!;

                    jsonPropertyInfo.EnsureChildOf(this);
                    jsonPropertyInfo.EnsureConfigured();
                }

                if (converter.ConstructorIsParameterized)
                {
                    InitializeConstructorParameters(GetParameterInfoValues(), sourceGenMode: Options.SerializerContext != null);
                }
            }
        }

#if DEBUG
        internal string GetPropertyDebugInfo(ReadOnlySpan<byte> unescapedPropertyName)
        {
            string propertyName = JsonHelpers.Utf8GetString(unescapedPropertyName);
            return $"propertyName = {propertyName}; DebugInfo={GetDebugInfo()}";
        }

        internal string GetDebugInfo()
        {
            ConverterStrategy strat = PropertyInfoForTypeInfo.ConverterStrategy;
            string jtiTypeName = GetType().Name;
            string typeName = Type.FullName!;
            bool propCacheInitialized = PropertyCache != null;

            StringBuilder sb = new();
            sb.AppendLine("{");
            sb.AppendLine($"  GetType: {jtiTypeName},");
            sb.AppendLine($"  Type: {typeName},");
            sb.AppendLine($"  ConverterStrategy: {strat},");
            sb.AppendLine($"  IsConfigured: {_isConfigured},");
            sb.AppendLine($"  HasPropertyCache: {propCacheInitialized},");

            if (propCacheInitialized)
            {
                sb.AppendLine("  Properties: {");
                foreach (var property in PropertyCache!.List)
                {
                    JsonPropertyInfo pi = property.Value!;
                    sb.AppendLine($"    {property.Key}:");
                    sb.AppendLine($"{pi.GetDebugInfo(indent: 6)},");
                }

                sb.AppendLine("  },");
            }

            sb.AppendLine("}");
            return sb.ToString();
        }
#endif

        internal virtual void LateAddProperties() { }

        /// <summary>
        /// Creates JsonTypeInfo
        /// </summary>
        /// <typeparam name="T">Type for which JsonTypeInfo stores metadata for</typeparam>
        /// <param name="options">Options associated with JsonTypeInfo</param>
        /// <returns>JsonTypeInfo instance</returns>
        [RequiresUnreferencedCode("JSON serialization and deserialization might require types that cannot be statically analyzed and might need runtime code generation. Use generic overload or System.Text.Json source generation for native AOT applications.")]
        [RequiresDynamicCode("JSON serialization and deserialization might require types that cannot be statically analyzed and might need runtime code generation. Use generic overload or System.Text.Json source generation for native AOT applications.")]
        public static JsonTypeInfo<T> CreateJsonTypeInfo<T>(JsonSerializerOptions options)
        {
            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            return new CustomJsonTypeInfo<T>(options);
        }

        private static MethodInfo? s_createJsonTypeInfo;

        /// <summary>
        /// Creates JsonTypeInfo
        /// </summary>
        /// <param name="type">Type for which JsonTypeInfo stores metadata for</param>
        /// <param name="options">Options associated with JsonTypeInfo</param>
        /// <returns>JsonTypeInfo instance</returns>
        [RequiresUnreferencedCode("JSON serialization and deserialization might require types that cannot be statically analyzed and might need runtime code generation. Use generic overload or System.Text.Json source generation for native AOT applications.")]
        [RequiresDynamicCode("JSON serialization and deserialization might require types that cannot be statically analyzed and might need runtime code generation. Use generic overload or System.Text.Json source generation for native AOT applications.")]
        public static JsonTypeInfo CreateJsonTypeInfo(Type type, JsonSerializerOptions options)
        {
            if (type == null)
            {
                throw new ArgumentNullException(nameof(type));
            }

            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            if (IsInvalidForSerialization(type))
            {
                ThrowHelper.ThrowArgumentException_CannotSerializeInvalidType(nameof(type), type, null, null);
            }

            s_createJsonTypeInfo ??= typeof(JsonTypeInfo).GetMethod(nameof(CreateJsonTypeInfo), new Type[] { typeof(JsonSerializerOptions) })!;
            return (JsonTypeInfo)s_createJsonTypeInfo.MakeGenericMethod(type)
                .Invoke(null, new object[] { options })!;
        }

        /// <summary>
        /// Creates JsonPropertyInfo
        /// </summary>
        /// <param name="propertyType">Type of the property</param>
        /// <param name="name">Name of the property</param>
        /// <returns>JsonPropertyInfo instance</returns>
        public JsonPropertyInfo CreateJsonPropertyInfo(Type propertyType, string name)
        {
            if (propertyType == null)
            {
                throw new ArgumentNullException(nameof(propertyType));
            }

            if (name == null)
            {
                throw new ArgumentNullException(nameof(name));
            }

            if (IsInvalidForSerialization(propertyType))
            {
                ThrowHelper.ThrowArgumentException_CannotSerializeInvalidType(nameof(propertyType), propertyType, Type, name);
            }

            JsonConverter converter = GetConverter(propertyType,
                parentClassType: null,
                memberInfo: null,
                options: Options,
                out _);

            JsonPropertyInfo propertyInfo = CreateProperty(
                declaredPropertyType: propertyType,
                memberInfo: null,
                parentClassType: Type,
                isVirtual: false,
                converter: converter,
                options: Options,
                isUserDefinedProperty: true);

            propertyInfo.Name = name;

            return propertyInfo;
        }

        internal abstract JsonParameterInfoValues[] GetParameterInfoValues();

        internal void CacheMember(JsonPropertyInfo jsonPropertyInfo, JsonPropertyDictionary<JsonPropertyInfo>? propertyCache, ref Dictionary<string, JsonPropertyInfo>? ignoredMembers)
        {
            Debug.Assert(jsonPropertyInfo.ClrName != null, "ClrName can be null in custom JsonPropertyInfo instances and should never be passed in this method");
            string memberName = jsonPropertyInfo.ClrName;

            // The JsonPropertyNameAttribute or naming policy resulted in a collision.
            if (!propertyCache!.TryAdd(jsonPropertyInfo.Name, jsonPropertyInfo))
            {
                JsonPropertyInfo other = propertyCache[jsonPropertyInfo.Name]!;

                if (other.IsIgnored)
                {
                    // Overwrite previously cached property since it has [JsonIgnore].
                    propertyCache[jsonPropertyInfo.Name] = jsonPropertyInfo;
                }
                else if (
                    // Does the current property have `JsonIgnoreAttribute`?
                    !jsonPropertyInfo.IsIgnored &&
                    // Is the current property hidden by the previously cached property
                    // (with `new` keyword, or by overriding)?
                    other.ClrName != memberName &&
                    // Was a property with the same CLR name was ignored? That property hid the current property,
                    // thus, if it was ignored, the current property should be ignored too.
                    ignoredMembers?.ContainsKey(memberName) != true)
                {
                    // We throw if we have two public properties that have the same JSON property name, and neither have been ignored.
                    ThrowHelper.ThrowInvalidOperationException_SerializerPropertyNameConflict(Type, jsonPropertyInfo.Name);
                }
                // Ignore the current property.
            }

            if (jsonPropertyInfo.IsIgnored)
            {
                (ignoredMembers ??= new Dictionary<string, JsonPropertyInfo>()).Add(memberName, jsonPropertyInfo);
            }
        }

        private sealed class ParameterLookupKey
        {
            public ParameterLookupKey(string name, Type type)
            {
                Name = name;
                Type = type;
            }

            public string Name { get; }
            public Type Type { get; }

            public override int GetHashCode()
            {
                return StringComparer.OrdinalIgnoreCase.GetHashCode(Name);
            }

            public override bool Equals([NotNullWhen(true)] object? obj)
            {
                Debug.Assert(obj is ParameterLookupKey);

                ParameterLookupKey other = (ParameterLookupKey)obj;
                return Type == other.Type && string.Equals(Name, other.Name, StringComparison.OrdinalIgnoreCase);
            }
        }

        private sealed class ParameterLookupValue
        {
            public ParameterLookupValue(JsonPropertyInfo jsonPropertyInfo)
            {
                JsonPropertyInfo = jsonPropertyInfo;
            }

            public string? DuplicateName { get; set; }
            public JsonPropertyInfo JsonPropertyInfo { get; }
        }

        internal void InitializeConstructorParameters(JsonParameterInfoValues[] jsonParameters, bool sourceGenMode = false)
        {
            var parameterCache = new JsonPropertyDictionary<JsonParameterInfo>(Options.PropertyNameCaseInsensitive, jsonParameters.Length);

            // Cache the lookup from object property name to JsonPropertyInfo using a case-insensitive comparer.
            // Case-insensitive is used to support both camel-cased parameter names and exact matches when C#
            // record types or anonymous types are used.
            // The property name key does not use [JsonPropertyName] or PropertyNamingPolicy since we only bind
            // the parameter name to the object property name and do not use the JSON version of the name here.
            var nameLookup = new Dictionary<ParameterLookupKey, ParameterLookupValue>(PropertyCache!.Count);

            foreach (KeyValuePair<string, JsonPropertyInfo?> kvp in PropertyCache.List)
            {
                JsonPropertyInfo jsonProperty = kvp.Value!;
                string propertyName = jsonProperty.ClrName ?? jsonProperty.Name;

                ParameterLookupKey key = new(propertyName, jsonProperty.PropertyType);
                ParameterLookupValue value = new(jsonProperty);

                if (!JsonHelpers.TryAdd(nameLookup, key, value))
                {
                    // More than one property has the same case-insensitive name and Type.
                    // Remember so we can throw a nice exception if this property is used as a parameter name.
                    ParameterLookupValue existing = nameLookup[key];
                    existing.DuplicateName = propertyName;
                }
            }

            foreach (JsonParameterInfoValues parameterInfo in jsonParameters)
            {
                ParameterLookupKey paramToCheck = new(parameterInfo.Name, parameterInfo.ParameterType);

                if (nameLookup.TryGetValue(paramToCheck, out ParameterLookupValue? matchingEntry))
                {
                    if (matchingEntry.DuplicateName != null)
                    {
                        // Multiple object properties cannot bind to the same constructor parameter.
                        ThrowHelper.ThrowInvalidOperationException_MultiplePropertiesBindToConstructorParameters(
                            Type,
                            parameterInfo.Name!,
                            matchingEntry.JsonPropertyInfo.Name,
                            matchingEntry.DuplicateName);
                    }

                    Debug.Assert(matchingEntry.JsonPropertyInfo != null);
                    JsonPropertyInfo jsonPropertyInfo = matchingEntry.JsonPropertyInfo;
                    JsonParameterInfo jsonParameterInfo = CreateConstructorParameter(parameterInfo, jsonPropertyInfo, sourceGenMode, Options);
                    parameterCache.Add(jsonPropertyInfo.Name, jsonParameterInfo);
                }
                // It is invalid for the extension data property to bind with a constructor argument.
                else if (DataExtensionProperty != null &&
                    StringComparer.OrdinalIgnoreCase.Equals(paramToCheck.Name, DataExtensionProperty.Name))
                {
                    Debug.Assert(DataExtensionProperty.ClrName != null, "Custom property info cannot be data extension property");
                    ThrowHelper.ThrowInvalidOperationException_ExtensionDataCannotBindToCtorParam(DataExtensionProperty.ClrName, DataExtensionProperty);
                }
            }

            ParameterCount = jsonParameters.Length;
            Volatile.Write(ref ParameterCache, parameterCache);
        }

        internal static void ValidateType(Type type, Type? parentClassType, MemberInfo? memberInfo, JsonSerializerOptions options)
        {
            if (IsInvalidForSerialization(type))
            {
                ThrowHelper.ThrowInvalidOperationException_CannotSerializeInvalidType(type, parentClassType, memberInfo);
            }
        }

        internal static bool IsInvalidForSerialization(Type type)
        {
            return type == typeof(void) || type.IsPointer || type.IsByRef || IsByRefLike(type) || type.ContainsGenericParameters;
        }

        private static bool IsByRefLike(Type type)
        {
#if BUILDING_INBOX_LIBRARY
            return type.IsByRefLike;
#else
            if (!type.IsValueType)
            {
                return false;
            }

            object[] attributes = type.GetCustomAttributes(inherit: false);

            for (int i = 0; i < attributes.Length; i++)
            {
                if (attributes[i].GetType().FullName == "System.Runtime.CompilerServices.IsByRefLikeAttribute")
                {
                    return true;
                }
            }

            return false;
#endif
        }

        internal void ConfigurePolymorphism(JsonConverter converter, JsonSerializerOptions options)
        {
#pragma warning disable CA2252 // This API requires opting into preview features
            Debug.Assert(Type != null);

            IJsonPolymorphicTypeConfiguration? configuration = null;

            // 1. Look up configuration from JsonSerializerOptions
            foreach (JsonPolymorphicTypeConfiguration config in options.PolymorphicTypeConfigurations)
            {
                if (config.BaseType == Type)
                {
                    configuration = config;
                }
            }

            // 2. Look up configuration from attributes
            configuration ??= AttributePolymorphicTypeConfiguration.Create(Type);

            // Construct the resolver from configuration.
            if (configuration != null)
            {
                PolymorphicTypeResolver = new PolymorphicTypeResolver(converter, configuration, options);
            }
#pragma warning restore CA2252 // This API requires opting into preview features
        }

        internal bool IsValidDataExtensionProperty(JsonPropertyInfo jsonPropertyInfo)
        {
            Type memberType = jsonPropertyInfo.PropertyType;

            bool typeIsValid = typeof(IDictionary<string, object>).IsAssignableFrom(memberType) ||
                typeof(IDictionary<string, JsonElement>).IsAssignableFrom(memberType) ||
                // Avoid a reference to typeof(JsonNode) to support trimming.
                (memberType.FullName == JsonObjectTypeName && ReferenceEquals(memberType.Assembly, GetType().Assembly));

            return typeIsValid && Options.GetConverterFromTypeInfo(memberType) != null;
        }

        internal JsonPropertyDictionary<JsonPropertyInfo> CreatePropertyCache(int capacity)
        {
            return new JsonPropertyDictionary<JsonPropertyInfo>(Options.PropertyNameCaseInsensitive, capacity);
        }

        // This method gets the runtime information for a given type or property.
        // The runtime information consists of the following:
        // - class type,
        // - element type (if the type is a collection),
        // - the converter (either native or custom), if one exists.
        internal static JsonConverter GetConverter(
            Type type,
            Type? parentClassType,
            MemberInfo? memberInfo,
            JsonSerializerOptions options,
            out JsonConverter? customConverter)
        {
            Debug.Assert(type != null);
            Debug.Assert(!IsInvalidForSerialization(type), $"Type `{type.FullName}` should already be validated.");
            customConverter = parentClassType != null ? options.GetCustomConverterFromMember(parentClassType, type, memberInfo) : null;
            return options.GetConverterForType(type);
        }

        internal static JsonConverter GetEffectiveConverter(
            Type type,
            Type? parentClassType,
            MemberInfo? memberInfo,
            JsonSerializerOptions options)
        {
            JsonConverter converter = GetConverter(type, parentClassType, memberInfo, options, out JsonConverter? customConverter);

            customConverter = options.ExpandFactoryConverter(customConverter, type);

            return customConverter ?? converter;
        }

        private static JsonParameterInfo CreateConstructorParameter(
            JsonParameterInfoValues parameterInfo,
            JsonPropertyInfo jsonPropertyInfo,
            bool sourceGenMode,
            JsonSerializerOptions options)
        {
            if (jsonPropertyInfo.IsIgnored)
            {
                return JsonParameterInfo.CreateIgnoredParameterPlaceholder(parameterInfo, jsonPropertyInfo, sourceGenMode);
            }

            JsonConverter converter = jsonPropertyInfo.EffectiveConverter;
            JsonParameterInfo jsonParameterInfo = converter.CreateJsonParameterInfo();

            jsonParameterInfo.Initialize(parameterInfo, jsonPropertyInfo, options);

            return jsonParameterInfo;
        }

        private static JsonTypeInfoKind GetTypeInfoKind(Type type, ConverterStrategy converterStrategy)
        {
            // System.Object is semi-polimorphic and will not respect Properties
            if (type == typeof(object))
            {
                return JsonTypeInfoKind.None;
            }

            return converterStrategy switch
            {
                ConverterStrategy.Object => JsonTypeInfoKind.Object,
                ConverterStrategy.Enumerable => JsonTypeInfoKind.Enumerable,
                ConverterStrategy.Dictionary => JsonTypeInfoKind.Dictionary,
                _ => JsonTypeInfoKind.None
            };
        }

        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        private string DebuggerDisplay => $"ConverterStrategy.{PropertyInfoForTypeInfo.ConverterStrategy}, {Type.Name}";
    }
}
