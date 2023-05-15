using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace NaughtyAttributes.Editor
{
    [CanEditMultipleObjects]
    [CustomEditor(typeof(UnityEngine.Object), true)]
    public class NaughtyInspector : UnityEditor.Editor
    {
        private List<SerializedProperty> _serializedProperties = new List<SerializedProperty>();
        private List<FieldInfo> _nonSerializedFields;
        private List<PropertyInfo> _nativeProperties;
        private IEnumerable<MethodInfo> _methods;
        private Dictionary<string, SavedBool> _foldouts = new Dictionary<string, SavedBool>();
        private Object objectWithDefaultValues; // Object to pull non-serialized default values from.
        private Component componentWithDefaultValues; // Component to pull non-serialized default values from.

        private List<string> _serializedProperyNames;
        private List<string> _nonSerializedFieldNames;
        private List<string> _nativePropertyNames;

        protected virtual void OnEnable()
        {
            if (!Application.isPlaying)
            {
                if (target.GetType().IsSubclassOf(typeof(MonoBehaviour)))
                {
                    // Create a temporary game object to pull non-serialized default values from.
                    var bind = target.GetType().GetField("_runSpeed", (BindingFlags)(-1));
                    var temporaryGameObject = Instantiate(((MonoBehaviour)target).gameObject); // Instantiate() only copies serialized fields

                    // Setup temporary object
                    temporaryGameObject.tag = "EditorOnly";
                    objectWithDefaultValues = temporaryGameObject;
                    objectWithDefaultValues.hideFlags = HideFlags.HideAndDontSave;

                    // Get inspected component type
                    componentWithDefaultValues = temporaryGameObject.GetComponent(target.GetType());
                }
                else if (target.GetType().IsSubclassOf(typeof(ScriptableObject)))
                {
                    // Create temporary ScriptableObject
                    objectWithDefaultValues = CreateInstance(target.GetType());
                }
            }

            _nonSerializedFields = ReflectionUtility.GetAllFields(
                target, f => f.GetCustomAttributes(typeof(ShowNonSerializedFieldAttribute), true).Length > 0).ToList();

            _nativeProperties = ReflectionUtility.GetAllProperties(
                target, p => p.GetCustomAttributes(typeof(ShowNativePropertyAttribute), true).Length > 0).ToList();

            _methods = ReflectionUtility.GetAllMethods(
                target, m => m.GetCustomAttributes(typeof(ButtonAttribute), true).Length > 0);

            _serializedProperyNames = null;
            _nonSerializedFieldNames = null;
            _nativePropertyNames = null;
        }

        protected virtual void OnDisable()
        {
            // Destroy temporary game object with non-serialized default values on disable
            if (objectWithDefaultValues != null) DestroyImmediate(objectWithDefaultValues);
            ReorderableListPropertyDrawer.Instance.ClearCache();
        }

        public override void OnInspectorGUI()
        {
            GetSerializedProperties(ref _serializedProperties);

            _serializedProperyNames ??= _serializedProperties.ConvertAll(x => ObjectNames.NicifyVariableName(x.name));
            _nonSerializedFieldNames ??= _nonSerializedFields.ConvertAll(x => ObjectNames.NicifyVariableName(x.Name));
            _nativePropertyNames ??= _nativeProperties.ConvertAll(x => ObjectNames.NicifyVariableName(x?.GetMethod.GetBackingFieldName()));

            bool anyNaughtyAttribute = _serializedProperties.Any(p => PropertyUtility.GetAttribute<INaughtyAttribute>(p) != null) || _nativeProperties.Count() != 0;
            if (!anyNaughtyAttribute)
            {
                DrawDefaultInspector();
            }
            else
            {
                DrawSerializedProperties();
            }

            DrawNonSerializedFields();
            DrawNativeProperties();
            DrawButtons();

            // Destroy temporary game object with non-serialized default values after first update of inspector gui
            if (objectWithDefaultValues != null) DestroyImmediate(objectWithDefaultValues);
        }

        protected void GetSerializedProperties(ref List<SerializedProperty> outSerializedProperties)
        {
            outSerializedProperties.Clear();
            using (var iterator = serializedObject.GetIterator())
            {
                if (iterator.NextVisible(true))
                {
                    do
                    {
                        outSerializedProperties.Add(serializedObject.FindProperty(iterator.name));
                    }
                    while (iterator.NextVisible(false));
                }
            }
        }

        protected int FindMatchingMember(List<string> members, string member)
        {
            var niceName = ObjectNames.NicifyVariableName(member);
            var index = members.FindIndex(0, x => x == niceName);
            return members.FindIndex(0, x => x == niceName);
        }

        protected void DrawSerializedProperties()
        {
            serializedObject.Update();

            // Draw non-grouped serialized properties
            foreach (var property in GetNonGroupedProperties(_serializedProperties))
            {
                if (property.name.Equals("m_Script", System.StringComparison.Ordinal))
                {
                    using (new EditorGUI.DisabledScope(disabled: true))
                    {
                        EditorGUILayout.PropertyField(property);
                    }
                }
                else
                {
                    int index = FindMatchingMember(_nativePropertyNames, property.name);
                    if (Application.isPlaying && index != -1) // display matching native property if in play mode instead of field
                        NaughtyEditorGUI.NativeProperty_Layout(serializedObject.targetObject, _nativeProperties[index], property.name);
                    else
                        NaughtyEditorGUI.PropertyField_Layout(property, includeChildren: true);
                }
            }

            // Draw grouped serialized properties
            foreach (var group in GetGroupedProperties(_serializedProperties))
            {
                IEnumerable<SerializedProperty> visibleProperties = group.Where(p => PropertyUtility.IsVisible(p));
                if (!visibleProperties.Any())
                {
                    continue;
                }

                NaughtyEditorGUI.BeginBoxGroup_Layout(group.Key);
                foreach (var property in visibleProperties)
                {
                    int index = FindMatchingMember(_nativePropertyNames, property.name);
                    if (Application.isPlaying && index != -1) // display matching native property if in play mode instead of field
                        NaughtyEditorGUI.NativeProperty_Layout(serializedObject.targetObject, _nativeProperties[index], property.name);
                    else
                        NaughtyEditorGUI.PropertyField_Layout(property, includeChildren: true);
                }

                NaughtyEditorGUI.EndBoxGroup_Layout();
            }

            // Draw foldout serialized properties
            foreach (var group in GetFoldoutProperties(_serializedProperties))
            {
                IEnumerable<SerializedProperty> visibleProperties = group.Where(p => PropertyUtility.IsVisible(p));
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
                    foreach (SerializedProperty property in visibleProperties)
                    {
                        int index = FindMatchingMember(_nativePropertyNames, property.name);
                        if (Application.isPlaying && index != -1) // display matching native property if in play mode instead of field
                            NaughtyEditorGUI.NativeProperty_Layout(serializedObject.targetObject, _nativeProperties[index], property.name);
                        else
                            NaughtyEditorGUI.PropertyField_Layout(property, includeChildren: true);
                    }
                    EditorGUI.indentLevel--;
                }
            }

            serializedObject.ApplyModifiedProperties();
        }

        protected void DrawNonSerializedStructOrField(object target, FieldInfo field)
        {
            // Set defaults for non-serialized component fields
            if (!Application.isPlaying && objectWithDefaultValues && !field.IsLiteral)
            {
                if (target.GetType().IsSubclassOf(typeof(MonoBehaviour)) && componentWithDefaultValues)
                    field.SetValue(target, field.GetValue(componentWithDefaultValues));
                else if (target.GetType().IsSubclassOf(typeof(ScriptableObject)))
                    field.SetValue(target, field.GetValue(objectWithDefaultValues));
            }

            if (field.FieldType.IsValueType && !field.FieldType.IsPrimitive && !field.FieldType.IsEnum && field.FieldType != typeof(LayerMask))
            {
                object subtarget = field.GetValue(target);

                if (!_foldouts.ContainsKey(field.Name))
                    _foldouts[field.Name] = new SavedBool($"{target.GetHashCode()}.{field.Name}", false);

                _foldouts[field.Name].Value = EditorGUILayout.Foldout(_foldouts[field.Name].Value, ObjectNames.NicifyVariableName(field.Name), true);
                if (_foldouts[field.Name].Value)
                {
                    EditorGUI.indentLevel++;
                    foreach (var subfield in field.FieldType.GetFields())
                    {
                        if (subfield.FieldType.IsValueType && !subfield.FieldType.IsPrimitive && !subfield.FieldType.IsEnum && subfield.FieldType != typeof(LayerMask))
                        {
                            DrawNonSerializedStructOrField(subtarget, subfield);
                        }
                        else
                        {
                            EditorGUI.BeginChangeCheck();
                            NaughtyEditorGUI.NonSerializedField_Layout(subtarget, subfield);
                            if (EditorGUI.EndChangeCheck() && target != null && field != null)
                                field.SetValue(target, subtarget);
                        }
                    }
                    EditorGUI.indentLevel--;
                }
            }
            else if (target is Object targetObject)
            {
                int index = FindMatchingMember(_nativePropertyNames, field.Name);
                if (Application.isPlaying && index != -1) // display matching native property if in play mode instead of field
                    NaughtyEditorGUI.NativeProperty_Layout(serializedObject.targetObject, _nativeProperties[index], field.Name);
                else
                    NaughtyEditorGUI.NonSerializedField_Layout(targetObject, field);
            }
            else
            {
                NaughtyEditorGUI.NonSerializedField_Layout(target, field);
            }
        }

        protected void DrawNonSerializedFields(bool drawHeader = false)
        {
            if (_nonSerializedFields.Any())
            {
                if (drawHeader)
                {
                    EditorGUILayout.Space();
                    EditorGUILayout.LabelField("Non-Serialized Fields", GetHeaderGUIStyle());
                    NaughtyEditorGUI.HorizontalLine(
                        EditorGUILayout.GetControlRect(false), HorizontalLineAttribute.DefaultHeight, HorizontalLineAttribute.DefaultColor.GetColor());
                }

                foreach (var field in GetNonGroupedProperties(_nonSerializedFields))
                {
                    DrawNonSerializedStructOrField(serializedObject.targetObject, field);
                }

                // Draw grouped non-serialized fields
                foreach (IGrouping<string, FieldInfo> group in GetGroupedProperties(_nonSerializedFields))
                {
                    NaughtyEditorGUI.BeginBoxGroup_Layout(group.Key);
                    foreach (var field in group)
                    {
                        DrawNonSerializedStructOrField(serializedObject.targetObject, field);
                    }

                    NaughtyEditorGUI.EndBoxGroup_Layout();
                }

                // Draw foldout non-serialized fields
                foreach (IGrouping<string, FieldInfo> group in GetFoldoutProperties(_nonSerializedFields))
                {
                    if (!_foldouts.ContainsKey(group.Key))
                    {
                        _foldouts[group.Key] = new SavedBool($"{target.GetInstanceID()}.{group.Key}", false);
                    }

                    _foldouts[group.Key].Value = EditorGUILayout.Foldout(_foldouts[group.Key].Value, group.Key, true);
                    if (_foldouts[group.Key].Value)
                    {
                        EditorGUI.indentLevel++;
                        foreach (var field in group)
                        {
                            DrawNonSerializedStructOrField(serializedObject.targetObject, field);
                        }
                        EditorGUI.indentLevel--;
                    }
                }
            }
        }

        protected void DrawNativeProperties(bool drawHeader = false)
        {
            if (_nativeProperties.Any())
            {
                if (drawHeader)
                {
                    EditorGUILayout.Space();
                    EditorGUILayout.LabelField("Native Properties", GetHeaderGUIStyle());
                    NaughtyEditorGUI.HorizontalLine(
                        EditorGUILayout.GetControlRect(false), HorizontalLineAttribute.DefaultHeight, HorizontalLineAttribute.DefaultColor.GetColor());
                }

                foreach (var property in _nativeProperties)
                {
                    // Don't display native properties that match existing serialized or non serialized fields - these are displayed inline
                    if (FindMatchingMember(_serializedProperyNames, property.Name) != -1 || FindMatchingMember(_nonSerializedFieldNames, property.Name) != -1)
                        continue;
                    NaughtyEditorGUI.NativeProperty_Layout(serializedObject.targetObject, property);
                }
            }
        }

        protected void DrawButtons(bool drawHeader = false)
        {
            if (_methods.Any())
            {
                if (drawHeader)
                {
                    EditorGUILayout.Space();
                    EditorGUILayout.LabelField("Buttons", GetHeaderGUIStyle());
                    NaughtyEditorGUI.HorizontalLine(
                        EditorGUILayout.GetControlRect(false), HorizontalLineAttribute.DefaultHeight, HorizontalLineAttribute.DefaultColor.GetColor());
                }

                foreach (var method in _methods)
                {
                    NaughtyEditorGUI.Button(serializedObject.targetObject, method);
                }
            }
        }

        private static IEnumerable<SerializedProperty> GetNonGroupedProperties(IEnumerable<SerializedProperty> properties)
        {
            return properties.Where(p => PropertyUtility.GetAttribute<IGroupAttribute>(p) == null);
        }

        private static IEnumerable<FieldInfo> GetNonGroupedProperties(IEnumerable<FieldInfo> fieldInfos)
        {
            return fieldInfos.Where(f => PropertyUtility.GetAttribute<IGroupAttribute>(f) == null);
        }

        private static IEnumerable<IGrouping<string, SerializedProperty>> GetGroupedProperties(IEnumerable<SerializedProperty> properties)
        {
            return properties
                .Where(p => PropertyUtility.GetAttribute<BoxGroupAttribute>(p) != null)
                .GroupBy(p => PropertyUtility.GetAttribute<BoxGroupAttribute>(p).Name);
        }

        private static IEnumerable<IGrouping<string, FieldInfo>> GetGroupedProperties(IEnumerable<FieldInfo> properties)
        {
            return properties
                .Where(f => PropertyUtility.GetAttribute<BoxGroupAttribute>(f) != null)
                .GroupBy(f => PropertyUtility.GetAttribute<BoxGroupAttribute>(f).Name);
        }

        private static IEnumerable<IGrouping<string, SerializedProperty>> GetFoldoutProperties(IEnumerable<SerializedProperty> properties)
        {
            return properties
                .Where(p => PropertyUtility.GetAttribute<FoldoutAttribute>(p) != null)
                .GroupBy(p => PropertyUtility.GetAttribute<FoldoutAttribute>(p).Name);
        }

        private static IEnumerable<IGrouping<string, FieldInfo>> GetFoldoutProperties(IEnumerable<FieldInfo> properties)
        {
            return properties
                .Where(f => PropertyUtility.GetAttribute<FoldoutAttribute>(f) != null)
                .GroupBy(f => PropertyUtility.GetAttribute<FoldoutAttribute>(f).Name);
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
