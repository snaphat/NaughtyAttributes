using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace NaughtyAttributes.Editor
{
    [CanEditMultipleObjects]
    [CustomEditor(typeof(Object), true)]
    public class NaughtyInspector : UnityEditor.Editor
    {
        private readonly Dictionary<string, SavedBool> _foldouts = new();

        private SerializedProperty[]                    _nonGroupedSerializedProperties;
        private IGrouping<string, SerializedProperty>[] _groupedSerializedProperties;
        private IGrouping<string, SerializedProperty>[] _foldoutSerializedProperties;

        private class InspectorTypeInfo
        {
            public bool AnyNaughtyAttribute;

            public Object ObjectWithDefaultValues; // Object to pull non-serialized default values from.

            public bool         HasMethods;
            public MethodInfo[] Methods;

            public bool                           HasNonSerializedFields;
            public FieldInfo[]                    NonGroupedNonSerializedFields;
            public IGrouping<string, FieldInfo>[] GroupedNonSerializedFields;
            public IGrouping<string, FieldInfo>[] FoldoutNonSerializedFields;

            public bool           HasNativeProperties;
            public PropertyInfo[] NonGroupedSerializedNativeProperties;
            public PropertyInfo[] GroupedSerializedNativeProperties;
            public PropertyInfo[] FoldoutSerializedNativeProperties;

            public PropertyInfo[] NonGroupedNonSerializedNativeProperties;
            public PropertyInfo[] GroupedNonSerializedNativeProperties;
            public PropertyInfo[] FoldoutNonSerializedNativeProperties;

            public PropertyInfo[] OtherNativeProperties;
        }

        private static readonly HashSet<(Type, string)>             NonGroupedPropertyPaths    = new();
        private static readonly Dictionary<(Type, string), string>  PropertyPathToBoxGroupName = new();
        private static readonly Dictionary<(Type, string), string>  PropertyPathToFoldoutName  = new();

        private static readonly Dictionary<Type, InspectorTypeInfo> InspectorTypeCache         = new();
        private                 InspectorTypeInfo                   _inspectorTypeInfo;

        protected virtual void OnEnable()
        {
            // Ignore null targets because they will result in SerializedObjectNotCreatableException when accessing the SerializedObject
            if (target == null) return;

            var type = target.GetType();

            var isAlreadyCached = InspectorTypeCache.TryGetValue(type, out _inspectorTypeInfo);
            if (!isAlreadyCached)
            {
                _inspectorTypeInfo       = new InspectorTypeInfo();
                InspectorTypeCache[type] = _inspectorTypeInfo;
            }

            var serializedProperties = GetSerializedProperties();

            if (isAlreadyCached)
            {
                // Remap to cached groups
                _nonGroupedSerializedProperties = GetNonGroupedPropertiesCached(type, serializedProperties);
                _groupedSerializedProperties    = GetGroupedPropertiesCached(type, serializedProperties);
                _foldoutSerializedProperties    = GetFoldoutPropertiesCached(type, serializedProperties);
                return;
            }

            // Uncached code-path below

            //Capture the original logging state
            var originalLogEnabled = Debug.unityLogger.logEnabled;

            try
            {
                // Disable logging so temporary object creation doesn't result in log entries
                Debug.unityLogger.logEnabled = false;


                // Create temporary MonoBehaviour or ScriptableObject
                Object temporaryObject = target switch
                {
                    MonoBehaviour mono => Instantiate(mono.gameObject).GetComponent(type),
                    ScriptableObject   => CreateInstance(type),
                    _                  => null
                };

                // Create untracked object and copy instance fields to get initializer and constructor defaults
                if (temporaryObject != null)
                {

                    var untracked = (Object)FormatterServices.GetUninitializedObject(type);

                    var fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    foreach (var field in fields)
                    {
                        if (field.IsLiteral) continue; // Skip constants, no need to skip static (non-instance field)

                        var value = field.GetValue(temporaryObject);
                        field.SetValue(untracked, value);
                    }

                    // Add to the cache
                    _inspectorTypeInfo.ObjectWithDefaultValues = untracked;

                    // Destroy the temporary (tracked) unity object
                    if (temporaryObject is MonoBehaviour monoBehaviour)
                        DestroyImmediate(monoBehaviour.gameObject); // Destroy the cloned GameObject
                    else
                        DestroyImmediate(temporaryObject); // ScriptableObject
                }
            }
            finally
            {
                // Reenable logging after object creation
                Debug.unityLogger.logEnabled = originalLogEnabled;
            }

            // Categorize serialized properties into ungrouped, [BoxGroup], and [Foldout] groups
            _nonGroupedSerializedProperties = GetNonGroupedProperties(serializedProperties);
            _groupedSerializedProperties    = GetGroupedProperties(serializedProperties);
            _foldoutSerializedProperties    = GetFoldoutProperties(serializedProperties);

            // Cache the property paths for non-grouped serialized fields per type (shared across instances)
            foreach (var property in _nonGroupedSerializedProperties)
            {
                var tuple = (type, property.propertyPath);
                NonGroupedPropertyPaths.Add(tuple);
            }

            // Cache the property paths for grouped serialized fields per type (shared across instances)
            foreach (var groupProperties in _groupedSerializedProperties)
            {
                foreach (var property in groupProperties)
                {
                    var tuple = (type, property.propertyPath);
                    PropertyPathToBoxGroupName.Add(tuple, groupProperties.Key);
                }
            }

            // Cache the property paths for foldout serialized fields per type (shared across instances)
            foreach (var foldoutProperties in _foldoutSerializedProperties)
            {
                foreach (var property in foldoutProperties)
                {
                    var tuple = (type, property.propertyPath);
                    PropertyPathToFoldoutName.Add(tuple, foldoutProperties.Key);
                }
            }

            // Get non-serialized fields with [ShowNonSerializedField]; used only locally, not cached
            var nonSerializedFields = ReflectionUtility.GetAllFields(
                target, static f => f.GetCustomAttributes(typeof(ShowNonSerializedFieldAttribute), true).Length > 0).ToList();

            // Get native properties with [ShowNativeProperty]; used only locally, not cached
            var nativeProperties = ReflectionUtility.GetAllProperties(
                target, static p => p.GetCustomAttributes(typeof(ShowNativePropertyAttribute), true).Length > 0).ToArray();

            // Get methods with [Button]; cached for running code
            _inspectorTypeInfo.Methods = ReflectionUtility.GetAllMethods(
                target, static m => m.GetCustomAttributes(typeof(ButtonAttribute), true).Length > 0).ToArray();


            // Create a lookup for non-serialized fields by name for faster access
            var nonSerializedFieldMap = nonSerializedFields.ToDictionary(f => f.Name, f => f);

            // Warn if a serialized field is marked with [ShowNonSerializedField], which has no effect
            foreach (var property in serializedProperties)
            {
                // Skip if the serialized property does not match any non-serialized field
                if (!nonSerializedFieldMap.TryGetValue(property.name, out var field)) continue;

                // Try to get the asset path
                var path = target switch
                {
                    MonoBehaviour monoBehaviour =>
                        AssetDatabase.GetAssetPath(MonoScript.FromMonoBehaviour(monoBehaviour)),
                    ScriptableObject scriptableObject =>
                        AssetDatabase.GetAssetPath(MonoScript.FromScriptableObject(scriptableObject)),
                    _ => null
                };

                // Build a warning message about the issue
                var message = "<color=red>Warning</color>: Ignoring <color=cyan>" + typeof(ShowNonSerializedFieldAttribute) + "</color> on serialized field <color=yellow>" + type + "." + field.Name + "</color>";
                if (path != null)
                    message += " in <a href=\"" + path + "\">" + path + "</a>!";
                else
                    message += "!";

                // Print the warning message
                Debug.Log(message);

                // Remove the field from the non-serialized field list
                nonSerializedFields.Remove(field);
            }

            // Cache presence flags for quick UI checks
            _inspectorTypeInfo.HasNonSerializedFields = nonSerializedFields.Count         != 0;
            _inspectorTypeInfo.HasNativeProperties    = nativeProperties.Length           != 0;
            _inspectorTypeInfo.HasMethods             = _inspectorTypeInfo.Methods.Length != 0;
            _inspectorTypeInfo.AnyNaughtyAttribute = serializedProperties.Any(static p => PropertyUtility.GetAttribute<INaughtyAttribute>(p) != null) ||
                                                     _inspectorTypeInfo.HasNonSerializedFields || _inspectorTypeInfo.HasNativeProperties || _inspectorTypeInfo.HasMethods;

            // Categorize and cache the nonserialized fields into ungrouped, [BoxGroup], and [Foldout] groups
            _inspectorTypeInfo.NonGroupedNonSerializedFields = GetNonGroupedFields(nonSerializedFields);
            _inspectorTypeInfo.GroupedNonSerializedFields    = GetGroupedFields(nonSerializedFields);
            _inspectorTypeInfo.FoldoutNonSerializedFields    = GetFoldoutFields(nonSerializedFields);

            // Match C# native properties (e.g., int, Vector3 getters/setters) to each grouped serialized property (field)
            // Entries with no matching property will be null
            // Only the names are used for mapping, so even if the SerializedProperty instances change, the mapping remains valid
            _inspectorTypeInfo.NonGroupedSerializedNativeProperties = MatchNativeProperties(_nonGroupedSerializedProperties, nativeProperties, static x => x.name);
            _inspectorTypeInfo.GroupedSerializedNativeProperties    = MatchNativeProperties(_groupedSerializedProperties,    nativeProperties, static x => x.Key);
            _inspectorTypeInfo.FoldoutSerializedNativeProperties    = MatchNativeProperties(_foldoutSerializedProperties,    nativeProperties, static x => x.Key);

            // Match C# native properties (e.g., int, Vector3 getters/setters) to each grouped non-serialized field
            // Entries with no matching property will be null
            _inspectorTypeInfo.NonGroupedNonSerializedNativeProperties = MatchNativeProperties(_inspectorTypeInfo.NonGroupedNonSerializedFields, nativeProperties, static x => x.Name);
            _inspectorTypeInfo.GroupedNonSerializedNativeProperties    = MatchNativeProperties(_inspectorTypeInfo.GroupedNonSerializedFields,    nativeProperties, static x => x.Key);
            _inspectorTypeInfo.FoldoutNonSerializedNativeProperties    = MatchNativeProperties(_inspectorTypeInfo.FoldoutNonSerializedFields,    nativeProperties, static x => x.Key);

            // Identify native C# properties not associated with any drawn field, so they can be rendered separately
            var allExcludedProperties = _inspectorTypeInfo.NonGroupedSerializedNativeProperties
                .Union(_inspectorTypeInfo.GroupedSerializedNativeProperties)
                .Union(_inspectorTypeInfo.FoldoutSerializedNativeProperties)
                .Union(_inspectorTypeInfo.NonGroupedNonSerializedNativeProperties)
                .Union(_inspectorTypeInfo.GroupedNonSerializedNativeProperties)
                .Union(_inspectorTypeInfo.FoldoutNonSerializedNativeProperties)
                .ToList();

            // Collect native properties that aren't matched to any serialized or non-serialized field, for separate display
            _inspectorTypeInfo.OtherNativeProperties = nativeProperties.Except(allExcludedProperties).ToArray();
        }

        protected virtual void OnDisable()
        {
            ReorderableListPropertyDrawer.Instance.ClearCache();
        }

        public override void OnInspectorGUI()
        {
            if (_inspectorTypeInfo is not { AnyNaughtyAttribute: true })
            {
                DrawDefaultInspector();
            }
            else
            {
                DrawSerializedProperties();
                DrawNonSerializedFields();
                DrawNativeProperties();
                DrawButtons();
            }
        }

        protected List<SerializedProperty> GetSerializedProperties()
        {
            var       outSerializedProperties = new List<SerializedProperty>();
            using var iterator                = serializedObject.GetIterator();
            if (iterator.NextVisible(true))
            {
                do
                {
                    outSerializedProperties.Add(serializedObject.FindProperty(iterator.name));
                } while (iterator.NextVisible(false));
            }

            return outSerializedProperties;
        }

        protected static PropertyInfo[] MatchNativeProperties<T>(T[] items, PropertyInfo[] nativeProperties, Func<T, string> getName)
        {
            var nameToProperty = new Dictionary<string, PropertyInfo>(nativeProperties.Length);
            foreach (var prop in nativeProperties)
            {
                var nicified = ObjectNames.NicifyVariableName(prop.Name);
                nameToProperty.TryAdd(nicified, prop);
            }

            var matchingProperties = new PropertyInfo[items.Length];
            for (var i = 0; i < items.Length; i++)
            {
                var nicified = ObjectNames.NicifyVariableName(getName(items[i]));
                nameToProperty.TryGetValue(nicified, out matchingProperties[i]);
            }

            return matchingProperties;
        }

        protected void DrawSerializedProperties()
        {
            serializedObject.Update();

            // Draw non-grouped serialized properties
            for (var i = 0; i < _nonGroupedSerializedProperties.Length; i++)
            {
                var property = _nonGroupedSerializedProperties[i];
                if (property.name.Equals("m_Script", StringComparison.Ordinal))
                {
                    using (new EditorGUI.DisabledScope(disabled: true))
                    {
                        EditorGUILayout.PropertyField(property);
                    }
                }
                else
                {
                    var nativeProperty = _inspectorTypeInfo.NonGroupedSerializedNativeProperties[i];
                    if (nativeProperty != null && EditorApplication.isPlayingOrWillChangePlaymode) // display matching native property if in play mode instead of field
                        NaughtyEditorGUI.NativeProperty_Layout(serializedObject.targetObject, nativeProperty, property.name);
                    else
                        NaughtyEditorGUI.PropertyField_Layout(property, includeChildren: true);
                }
            }

            // Draw grouped serialized properties
            for (var i = 0; i < _groupedSerializedProperties.Length; i++)
            {
                var group = _groupedSerializedProperties[i];
                var visibleProperties = group.Where(static p => PropertyUtility.IsVisible(p)).ToArray();
                if (!visibleProperties.Any())
                {
                    continue;
                }

                NaughtyEditorGUI.BeginBoxGroup_Layout(group.Key);
                foreach (var property in visibleProperties)
                {
                    var nativeProperty = _inspectorTypeInfo.GroupedSerializedNativeProperties[i];
                    if (nativeProperty != null && EditorApplication.isPlayingOrWillChangePlaymode) // display matching native property if in play mode instead of field
                        NaughtyEditorGUI.NativeProperty_Layout(serializedObject.targetObject, nativeProperty, property.name);
                    else
                        NaughtyEditorGUI.PropertyField_Layout(property, includeChildren: true);
                }

                NaughtyEditorGUI.EndBoxGroup_Layout();
            }

            // Draw foldout serialized properties
            for (var i = 0; i < _foldoutSerializedProperties.Length; i++)
            {
                var group = _foldoutSerializedProperties[i];
                var visibleProperties = group.Where(static p => PropertyUtility.IsVisible(p)).ToArray();
                if (!visibleProperties.Any())
                {
                    continue;
                }

                if (!_foldouts.ContainsKey(group.Key))
                {
                    _foldouts[group.Key] = new SavedBool($"{target.GetInstanceID()}.{group.Key}", false);
                }

                _foldouts[group.Key].Value = EditorGUILayout.Foldout(_foldouts[group.Key].Value, group.Key, true);
                if (_foldouts[group.Key].Value)
                {
                    EditorGUI.indentLevel++;
                    foreach (var property in visibleProperties)
                    {
                        var nativeProperty = _inspectorTypeInfo.FoldoutSerializedNativeProperties[i];
                        if (nativeProperty != null && EditorApplication.isPlayingOrWillChangePlaymode) // display matching native property if in play mode instead of field
                            NaughtyEditorGUI.NativeProperty_Layout(serializedObject.targetObject, nativeProperty, property.name);
                        else
                            NaughtyEditorGUI.PropertyField_Layout(property, includeChildren: true);
                    }

                    EditorGUI.indentLevel--;
                }
            }

            serializedObject.ApplyModifiedProperties();
        }

        protected void DrawNonSerializedStructOrField(object targetObj, FieldInfo field, PropertyInfo nativeProperty)
        {
            // Set defaults for non-serialized component fields
            if (!EditorApplication.isPlayingOrWillChangePlaymode && _inspectorTypeInfo.ObjectWithDefaultValues && !field.IsLiteral)
            {
                if (targetObj.GetType().IsSubclassOf(typeof(MonoBehaviour)) || targetObj.GetType().IsSubclassOf(typeof(ScriptableObject)))
                    field.SetValue(targetObj, field.GetValue(_inspectorTypeInfo.ObjectWithDefaultValues));
            }

            if (field.FieldType is { IsValueType: true, IsPrimitive: false, IsEnum: false } && field.FieldType != typeof(LayerMask))
            {
                var subtarget = field.GetValue(targetObj);

                if (!_foldouts.ContainsKey(field.Name))
                    _foldouts[field.Name] = new SavedBool($"{targetObj.GetHashCode()}.{field.Name}", false);

                _foldouts[field.Name].Value = EditorGUILayout.Foldout(_foldouts[field.Name].Value, ObjectNames.NicifyVariableName(field.Name), true);
                if (_foldouts[field.Name].Value)
                {
                    EditorGUI.indentLevel++;
                    foreach (var subfield in field.FieldType.GetFields())
                    {
                        if (subfield.FieldType is { IsValueType: true, IsPrimitive: false, IsEnum: false } && subfield.FieldType != typeof(LayerMask))
                        {
                            DrawNonSerializedStructOrField(subtarget, subfield, null);
                        }
                        else
                        {
                            EditorGUI.BeginChangeCheck();
                            NaughtyEditorGUI.NonSerializedField_Layout(subtarget, subfield);
                            if (EditorGUI.EndChangeCheck() && targetObj != null)
                                field.SetValue(targetObj, subtarget);
                        }
                    }

                    EditorGUI.indentLevel--;
                }
            }
            else if (targetObj is Object targetObject)
            {
                if (nativeProperty != null && EditorApplication.isPlayingOrWillChangePlaymode) // display matching native property if in play mode instead of field
                    NaughtyEditorGUI.NativeProperty_Layout(serializedObject.targetObject, nativeProperty, field.Name);
                else
                    NaughtyEditorGUI.NonSerializedField_Layout(targetObject, field);
            }
            else
            {
                NaughtyEditorGUI.NonSerializedField_Layout(targetObj, field);
            }
        }

        protected void DrawNonSerializedFields(bool drawHeader = false)
        {
            if (!_inspectorTypeInfo.HasNonSerializedFields) return;

            if (drawHeader)
            {
                EditorGUILayout.Space();
                EditorGUILayout.LabelField("Non-Serialized Fields", GetHeaderGUIStyle());
                NaughtyEditorGUI.HorizontalLine(EditorGUILayout.GetControlRect(false), HorizontalLineAttribute.DefaultHeight, HorizontalLineAttribute.DefaultColor.GetColor());
            }

            // Draw non-grouped serialized fields
            for (var i = 0; i < _inspectorTypeInfo.NonGroupedNonSerializedFields.Length; i++)
            {
                var field = _inspectorTypeInfo.NonGroupedNonSerializedFields[i];
                DrawNonSerializedStructOrField(serializedObject.targetObject, field, _inspectorTypeInfo.NonGroupedNonSerializedNativeProperties[i]);
            }

            // Draw grouped non-serialized fields
            for (var i = 0; i < _inspectorTypeInfo.GroupedNonSerializedFields.Length; i++)
            {
                var group = _inspectorTypeInfo.GroupedNonSerializedFields[i];
                NaughtyEditorGUI.BeginBoxGroup_Layout(group.Key);
                foreach (var field in group)
                {
                    DrawNonSerializedStructOrField(serializedObject.targetObject, field, _inspectorTypeInfo.GroupedNonSerializedNativeProperties[i]);
                }

                NaughtyEditorGUI.EndBoxGroup_Layout();
            }

            // Draw foldout non-serialized fields
            for (var i = 0; i < _inspectorTypeInfo.FoldoutNonSerializedFields.Length; i++)
            {
                var group = _inspectorTypeInfo.FoldoutNonSerializedFields[i];
                if (!_foldouts.ContainsKey(group.Key))
                {
                    _foldouts[group.Key] = new SavedBool($"{target.GetInstanceID()}.{group.Key}", false);
                }

                _foldouts[group.Key].Value = EditorGUILayout.Foldout(_foldouts[group.Key].Value, group.Key, true);
                if (!_foldouts[group.Key].Value) continue;

                EditorGUI.indentLevel++;
                foreach (var field in group)
                {
                    DrawNonSerializedStructOrField(serializedObject.targetObject, field, _inspectorTypeInfo.FoldoutNonSerializedNativeProperties[i]);
                }

                EditorGUI.indentLevel--;
            }
        }

        protected void DrawNativeProperties(bool drawHeader = false)
        {
            if (!_inspectorTypeInfo.HasNativeProperties) return;

            if (drawHeader)
            {
                EditorGUILayout.Space();
                EditorGUILayout.LabelField("Native Properties", GetHeaderGUIStyle());
                NaughtyEditorGUI.HorizontalLine(EditorGUILayout.GetControlRect(false), HorizontalLineAttribute.DefaultHeight, HorizontalLineAttribute.DefaultColor.GetColor());
            }

            // Render only native properties not already displayed inline with serialized or non-serialized fields
            foreach (var property in _inspectorTypeInfo.OtherNativeProperties)
            {
                NaughtyEditorGUI.NativeProperty_Layout(serializedObject.targetObject, property);
            }
        }

        protected void DrawButtons(bool drawHeader = false)
        {
            if (!_inspectorTypeInfo.HasMethods) return;

            if (drawHeader)
            {
                EditorGUILayout.Space();
                EditorGUILayout.LabelField("Buttons", GetHeaderGUIStyle());
                NaughtyEditorGUI.HorizontalLine(EditorGUILayout.GetControlRect(false), HorizontalLineAttribute.DefaultHeight, HorizontalLineAttribute.DefaultColor.GetColor());
            }

            foreach (var method in _inspectorTypeInfo.Methods)
            {
                NaughtyEditorGUI.Button(serializedObject.targetObject, method);
            }
        }

        private static SerializedProperty[] GetNonGroupedPropertiesCached(Type type, IEnumerable<SerializedProperty> properties)
        {
            return properties.Where(p => NonGroupedPropertyPaths.Contains((type, p.propertyPath))).ToArray();
        }

        private static SerializedProperty[] GetNonGroupedProperties(IEnumerable<SerializedProperty> properties)
        {
            return properties.Where(static p => PropertyUtility.GetAttribute<IGroupAttribute>(p) == null).ToArray();
        }

        private static FieldInfo[] GetNonGroupedFields(IEnumerable<FieldInfo> fieldInfos)
        {
            return fieldInfos.Where(static f => PropertyUtility.GetAttribute<IGroupAttribute>(f) == null).ToArray();
        }

        private static IGrouping<string, SerializedProperty>[] GetGroupedPropertiesCached(Type type, IEnumerable<SerializedProperty> properties)
        {
            return properties
                  .Select(p => (Property: p,
                                Key: PropertyPathToBoxGroupName.GetValueOrDefault((type, p.propertyPath))))
                  .Where(static x => x.Key != null)
                  .GroupBy(static x => x.Key!, static x => x.Property) .ToArray();
        }

        private static IGrouping<string, SerializedProperty>[] GetGroupedProperties(IEnumerable<SerializedProperty> properties)
        {
            return properties
                  .Select(static p => (Property: p,
                                       Key: PropertyUtility.GetAttribute<BoxGroupAttribute>(p)?.Name))
                  .Where(static x => x.Key != null)
                  .GroupBy(static x => x.Key!, static x => x.Property) .ToArray();
        }

        private static IGrouping<string, FieldInfo>[] GetGroupedFields(IEnumerable<FieldInfo> properties)
        {
            return properties
                  .Select(static p => (Property: p,
                                       Key: PropertyUtility.GetAttribute<BoxGroupAttribute>(p)?.Name))
                  .Where(static x => x.Key != null)
                  .GroupBy(static x => x.Key!, static x => x.Property) .ToArray();
        }

        private static IGrouping<string, SerializedProperty>[] GetFoldoutPropertiesCached(Type type, IEnumerable<SerializedProperty> properties)
        {
            return properties
                  .Select(p => (Property: p,
                                Key: PropertyPathToFoldoutName.GetValueOrDefault((type, p.propertyPath))))
                  .Where(static x => x.Key != null)
                  .GroupBy(static x => x.Key!, static x => x.Property) .ToArray();
        }

        private static IGrouping<string, SerializedProperty>[] GetFoldoutProperties(IEnumerable<SerializedProperty> properties)
        {
            return properties
                  .Select(static p => (Property: p,
                                       Key: PropertyUtility.GetAttribute<FoldoutAttribute>(p)?.Name))
                  .Where(static x => x.Key != null)
                  .GroupBy(static x => x.Key!, static x => x.Property) .ToArray();
        }

        private static IGrouping<string, FieldInfo>[] GetFoldoutFields(IEnumerable<FieldInfo> properties)
        {
            return properties
                  .Select(static p => (Property: p,
                                       Key: PropertyUtility.GetAttribute<FoldoutAttribute>(p)?.Name))
                  .Where(static x => x.Key != null)
                  .GroupBy(static x => x.Key!, static x => x.Property) .ToArray();
        }

        private static GUIStyle GetHeaderGUIStyle()
        {
            GUIStyle style = new GUIStyle(EditorStyles.centeredGreyMiniLabel);
            style.fontStyle = FontStyle.Bold;
            style.alignment = TextAnchor.UpperCenter;

            return style;
        }
    }
}
