using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.Experimental.SceneManagement;
using UnityEditor.SceneManagement;
using UnityEditorInternal;
using UnityEngine;

namespace NaughtyAttributes.Editor
{
    public static class NaughtyEditorGUI
    {
        public const float IndentLength = 15.0f;
        public const float HorizontalSpacing = 2.0f;

        private static GUIStyle _buttonStyle = new GUIStyle(GUI.skin.button) { richText = true };

        private delegate void PropertyFieldFunction(Rect rect, SerializedProperty property, GUIContent label, bool includeChildren);

        public static void PropertyField(Rect rect, SerializedProperty property, bool includeChildren)
        {
            PropertyField_Implementation(rect, property, includeChildren, DrawPropertyField);
        }

        public static void PropertyField_Layout(SerializedProperty property, bool includeChildren)
        {
            Rect dummyRect = new Rect();
            PropertyField_Implementation(dummyRect, property, includeChildren, DrawPropertyField_Layout);
        }

        private static void DrawPropertyField(Rect rect, SerializedProperty property, GUIContent label, bool includeChildren)
        {
            EditorGUI.PropertyField(rect, property, label, includeChildren);
        }

        private static void DrawPropertyField_Layout(Rect rect, SerializedProperty property, GUIContent label, bool includeChildren)
        {
            EditorGUILayout.PropertyField(property, label, includeChildren);
        }

        private static void PropertyField_Implementation(Rect rect, SerializedProperty property, bool includeChildren, PropertyFieldFunction propertyFieldFunction)
        {
            SpecialCaseDrawerAttribute specialCaseAttribute = PropertyUtility.GetAttribute<SpecialCaseDrawerAttribute>(property);
            if (specialCaseAttribute != null)
            {
                specialCaseAttribute.GetDrawer().OnGUI(rect, property);
            }
            else
            {
                // Check if visible
                bool visible = PropertyUtility.IsVisible(property);
                if (!visible)
                {
                    return;
                }

                // Validate
                ValidatorAttribute[] validatorAttributes = PropertyUtility.GetAttributes<ValidatorAttribute>(property);
                foreach (var validatorAttribute in validatorAttributes)
                {
                    validatorAttribute.GetValidator().ValidateProperty(property);
                }

                // Check if enabled and draw
                EditorGUI.BeginChangeCheck();
                bool enabled = PropertyUtility.IsEnabled(property);

                using (new EditorGUI.DisabledScope(disabled: !enabled))
                {
                    propertyFieldFunction.Invoke(rect, property, PropertyUtility.GetLabel(property), includeChildren);
                }

                // Call OnValueChanged callbacks
                if (EditorGUI.EndChangeCheck())
                {
                    PropertyUtility.CallOnValueChangedCallbacks(property);
                }
            }
        }

        public static float GetIndentLength(Rect sourceRect)
        {
            Rect indentRect = EditorGUI.IndentedRect(sourceRect);
            float indentLength = indentRect.x - sourceRect.x;

            return indentLength;
        }

        public static void BeginBoxGroup_Layout(string label = "")
        {
            EditorGUILayout.BeginVertical(GUI.skin.box);
            if (!string.IsNullOrEmpty(label))
            {
                EditorGUILayout.LabelField(label, EditorStyles.boldLabel);
            }
        }

        public static void EndBoxGroup_Layout()
        {
            EditorGUILayout.EndVertical();
        }

        /// <summary>
        /// Creates a dropdown
        /// </summary>
        /// <param name="rect">The rect the defines the position and size of the dropdown in the inspector</param>
        /// <param name="serializedObject">The serialized object that is being updated</param>
        /// <param name="target">The target object that contains the dropdown</param>
        /// <param name="dropdownField">The field of the target object that holds the currently selected dropdown value</param>
        /// <param name="label">The label of the dropdown</param>
        /// <param name="selectedValueIndex">The index of the value from the values array</param>
        /// <param name="values">The values of the dropdown</param>
        /// <param name="displayOptions">The display options for the values</param>
        public static void Dropdown(
            Rect rect, SerializedObject serializedObject, object target, FieldInfo dropdownField,
            string label, int selectedValueIndex, object[] values, string[] displayOptions)
        {
            EditorGUI.BeginChangeCheck();

            int newIndex = EditorGUI.Popup(rect, label, selectedValueIndex, displayOptions);
            object newValue = values[newIndex];

            object dropdownValue = dropdownField.GetValue(target);
            if (dropdownValue == null || !dropdownValue.Equals(newValue))
            {
                Undo.RecordObject(serializedObject.targetObject, "Dropdown");

                // TODO: Problem with structs, because they are value type.
                // The solution is to make boxing/unboxing but unfortunately I don't know the compile time type of the target object
                dropdownField.SetValue(target, newValue);
            }
        }

        public static void Button(UnityEngine.Object target, MethodInfo methodInfo)
        {
            bool visible = ButtonUtility.IsVisible(target, methodInfo);
            if (!visible)
            {
                return;
            }

            if (methodInfo.GetParameters().All(p => p.IsOptional))
            {
                ButtonAttribute buttonAttribute = (ButtonAttribute)methodInfo.GetCustomAttributes(typeof(ButtonAttribute), true)[0];
                string buttonText = string.IsNullOrEmpty(buttonAttribute.Text) ? ObjectNames.NicifyVariableName(methodInfo.Name) : buttonAttribute.Text;

                bool buttonEnabled = ButtonUtility.IsEnabled(target, methodInfo);

                EButtonEnableMode mode = buttonAttribute.SelectedEnableMode;
                buttonEnabled &=
                    mode == EButtonEnableMode.Always ||
                    mode == EButtonEnableMode.Editor && !Application.isPlaying ||
                    mode == EButtonEnableMode.Playmode && Application.isPlaying;

                bool methodIsCoroutine = methodInfo.ReturnType == typeof(IEnumerator);
                if (methodIsCoroutine)
                {
                    buttonEnabled &= (Application.isPlaying ? true : false);
                }

                EditorGUI.BeginDisabledGroup(!buttonEnabled);

                if (GUILayout.Button(buttonText, _buttonStyle))
                {
                    object[] defaultParams = methodInfo.GetParameters().Select(p => p.DefaultValue).ToArray();
                    IEnumerator methodResult = methodInfo.Invoke(target, defaultParams) as IEnumerator;

                    if (!Application.isPlaying)
                    {
                        // Set target object and scene dirty to serialize changes to disk
                        EditorUtility.SetDirty(target);

                        PrefabStage stage = PrefabStageUtility.GetCurrentPrefabStage();
                        if (stage != null)
                        {
                            // Prefab mode
                            EditorSceneManager.MarkSceneDirty(stage.scene);
                        }
                        else
                        {
                            // Normal scene
                            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
                        }
                    }
                    else if (methodResult != null && target is MonoBehaviour behaviour)
                    {
                        behaviour.StartCoroutine(methodResult);
                    }
                }

                EditorGUI.EndDisabledGroup();
            }
            else
            {
                string warning = typeof(ButtonAttribute).Name + " works only on methods with no parameters";
                HelpBox_Layout(warning, MessageType.Warning, context: target, logToConsole: true);
            }
        }

        public static void NativeProperty_Layout(UnityEngine.Object target, PropertyInfo property)
        {
            object value = property.GetValue(target, null);

            if (value == null)
            {
                string warning = string.Format("{0} is null. {1} doesn't support reference types with null value", ObjectNames.NicifyVariableName(property.Name), typeof(ShowNativePropertyAttribute).Name);
                HelpBox_Layout(warning, MessageType.Warning, context: target);
            }
            else if (!Field_Layout(value, ObjectNames.NicifyVariableName(property.Name)))
            {
                string warning = string.Format("{0} doesn't support {1} types", typeof(ShowNativePropertyAttribute).Name, property.PropertyType.Name);
                HelpBox_Layout(warning, MessageType.Warning, context: target);
            }
        }

        public static void NonSerializedField_Layout(UnityEngine.Object target, FieldInfo field)
        {
            object value = field.GetValue(target);

            if (value == null)
            {
                string warning = string.Format("{0} is null. {1} doesn't support reference types with null value", ObjectNames.NicifyVariableName(field.Name), typeof(ShowNonSerializedFieldAttribute).Name);
                HelpBox_Layout(warning, MessageType.Warning, context: target);
            }
            else if (!Field_Layout(value, ObjectNames.NicifyVariableName(field.Name), target, field))
            {
                string warning = string.Format("{0} doesn't support {1} types", typeof(ShowNonSerializedFieldAttribute).Name, field.FieldType.Name);
                HelpBox_Layout(warning, MessageType.Warning, context: target);
            }
        }

        public static void NonSerializedField_Layout(object target, FieldInfo field)
        {
            object value = field.GetValue(target);

            if (value == null) return;

            Field_Layout(value, ObjectNames.NicifyVariableName(field.Name), target, field);
        }

        public static void HorizontalLine(Rect rect, float height, Color color)
        {
            rect.height = height;
            EditorGUI.DrawRect(rect, color);
        }

        public static void HelpBox(Rect rect, string message, MessageType type, UnityEngine.Object context = null, bool logToConsole = false)
        {
            EditorGUI.HelpBox(rect, message, type);

            if (logToConsole)
            {
                DebugLogMessage(message, type, context);
            }
        }

        public static void HelpBox_Layout(string message, MessageType type, UnityEngine.Object context = null, bool logToConsole = false)
        {
            EditorGUILayout.HelpBox(message, type);

            if (logToConsole)
            {
                DebugLogMessage(message, type, context);
            }
        }

        public static bool Field_Layout(object value, string label, object target = null, FieldInfo field = null)
        {
            using (new EditorGUI.DisabledScope(disabled: target == null || field == null))
            {
                bool isDrawn = true;
                Type valueType = value.GetType();

                if (valueType == typeof(bool))
                {
                    EditorGUI.BeginChangeCheck();
                    var ret = EditorGUILayout.Toggle(label, (bool)value);
                    if (EditorGUI.EndChangeCheck() && target != null && field != null)
                        field.SetValue(target, ret);
                }
                else if (valueType == typeof(short))
                {
                    EditorGUI.BeginChangeCheck();
                    var ret = EditorGUILayout.IntField(label, (short)value);
                    if (EditorGUI.EndChangeCheck() && target != null && field != null)
                        field.SetValue(target, ret);
                }
                else if (valueType == typeof(ushort))
                {
                    EditorGUI.BeginChangeCheck();
                    var ret = EditorGUILayout.IntField(label, (ushort)value);
                    if (EditorGUI.EndChangeCheck() && target != null && field != null)
                        field.SetValue(target, ret);
                }
                else if (valueType == typeof(int))
                {
                    EditorGUI.BeginChangeCheck();
                    var ret = EditorGUILayout.IntField(label, (int)value);
                    if (EditorGUI.EndChangeCheck() && target != null && field != null)
                        field.SetValue(target, ret);
                }
                else if (valueType == typeof(uint))
                {
                    EditorGUI.BeginChangeCheck();
                    var ret = EditorGUILayout.LongField(label, (uint)value);
                    if (EditorGUI.EndChangeCheck() && target != null && field != null)
                        field.SetValue(target, ret);
                }
                else if (valueType == typeof(long))
                {
                    EditorGUI.BeginChangeCheck();
                    var ret = EditorGUILayout.LongField(label, (long)value);
                    if (EditorGUI.EndChangeCheck() && target != null && field != null)
                        field.SetValue(target, ret);
                }
                else if (valueType == typeof(ulong))
                {
                    EditorGUI.BeginChangeCheck();
                    var ret = Convert.ToUInt64(EditorGUILayout.TextField(label, ((ulong)value).ToString()));
                    if (EditorGUI.EndChangeCheck() && target != null && field != null)
                        field.SetValue(target, ret);
                }
                else if (valueType == typeof(float))
                {
                    EditorGUI.BeginChangeCheck();
                    var ret = EditorGUILayout.FloatField(label, (float)value);
                    if (EditorGUI.EndChangeCheck() && target != null && field != null)
                        field.SetValue(target, ret);
                }
                else if (valueType == typeof(double))
                {
                    EditorGUI.BeginChangeCheck();
                    var ret = EditorGUILayout.DoubleField(label, (double)value);
                    if (EditorGUI.EndChangeCheck() && target != null && field != null)
                        field.SetValue(target, ret);
                }
                else if (valueType == typeof(string))
                {
                    EditorGUI.BeginChangeCheck();
                    var ret = EditorGUILayout.TextField(label, (string)value);
                    if (EditorGUI.EndChangeCheck() && target != null && field != null)
                        field.SetValue(target, ret);
                }
                else if (valueType == typeof(Vector2))
                {
                    EditorGUI.BeginChangeCheck();
                    var ret = EditorGUILayout.Vector2Field(label, (Vector2)value);
                    if (EditorGUI.EndChangeCheck() && target != null && field != null)
                        field.SetValue(target, ret);
                }
                else if (valueType == typeof(Vector3))
                {
                    EditorGUI.BeginChangeCheck();
                    var ret = EditorGUILayout.Vector3Field(label, (Vector3)value);
                    if (EditorGUI.EndChangeCheck() && target != null && field != null)
                        field.SetValue(target, ret);
                }
                else if (valueType == typeof(Vector4))
                {
                    EditorGUI.BeginChangeCheck();
                    var ret = EditorGUILayout.Vector4Field(label, (Vector4)value);
                    if (EditorGUI.EndChangeCheck() && target != null && field != null)
                        field.SetValue(target, ret);
                }
                else if (valueType == typeof(Vector2Int))
                {
                    EditorGUI.BeginChangeCheck();
                    var ret = EditorGUILayout.Vector2IntField(label, (Vector2Int)value);
                    if (EditorGUI.EndChangeCheck() && target != null && field != null)
                        field.SetValue(target, ret);
                }
                else if (valueType == typeof(Vector3Int))
                {
                    EditorGUI.BeginChangeCheck();
                    var ret = EditorGUILayout.Vector3IntField(label, (Vector3Int)value);
                    if (EditorGUI.EndChangeCheck() && target != null && field != null)
                        field.SetValue(target, ret);
                }
                else if (valueType == typeof(Color))
                {
                    EditorGUI.BeginChangeCheck();
                    var ret = EditorGUILayout.ColorField(label, (Color)value);
                    if (EditorGUI.EndChangeCheck() && target != null && field != null)
                        field.SetValue(target, ret);
                }
                else if (valueType == typeof(Bounds))
                {
                    EditorGUI.BeginChangeCheck();
                    var ret = EditorGUILayout.BoundsField(label, (Bounds)value);
                    if (EditorGUI.EndChangeCheck() && target != null && field != null)
                        field.SetValue(target, ret);
                }
                else if (valueType == typeof(Rect))
                {
                    EditorGUI.BeginChangeCheck();
                    var ret = EditorGUILayout.RectField(label, (Rect)value);
                    if (EditorGUI.EndChangeCheck() && target != null && field != null)
                        field.SetValue(target, ret);
                }
                else if (valueType == typeof(RectInt))
                {
                    EditorGUI.BeginChangeCheck();
                    var ret = EditorGUILayout.RectIntField(label, (RectInt)value);
                    if (EditorGUI.EndChangeCheck() && target != null && field != null)
                        field.SetValue(target, ret);
                }
                else if (valueType == typeof(LayerMask))
                {
                    EditorGUI.BeginChangeCheck();
                    var ret = EditorGUILayout.MaskField(label, InternalEditorUtility.LayerMaskToConcatenatedLayersMask((LayerMask)value), InternalEditorUtility.layers);
                    if (EditorGUI.EndChangeCheck() && target != null && field != null)
                        field.SetValue(target, (LayerMask)ret);
                }
                else if (typeof(UnityEngine.Object).IsAssignableFrom(valueType))
                {
                    EditorGUI.BeginChangeCheck();
                    var ret = EditorGUILayout.ObjectField(label, (UnityEngine.Object)value, valueType, true);
                    if (EditorGUI.EndChangeCheck() && target != null && field != null)
                        field.SetValue(target, ret);
                }
                else if (valueType.BaseType == typeof(Enum))
                {
                    EditorGUI.BeginChangeCheck();
                    var ret = EditorGUILayout.EnumPopup(label, (Enum)value);
                    if (EditorGUI.EndChangeCheck() && target != null && field != null)
                        field.SetValue(target, ret);
                }
                else if (valueType.BaseType == typeof(System.Reflection.TypeInfo))
                {
                    EditorGUI.BeginChangeCheck();
                    var ret = EditorGUILayout.TextField(label, value.ToString());
                    if (EditorGUI.EndChangeCheck() && target != null && field != null)
                        field.SetValue(target, ret);
                }
                else
                {
                    isDrawn = false;
                }

                return isDrawn;
            }
        }

        private static void DebugLogMessage(string message, MessageType type, UnityEngine.Object context)
        {
            switch (type)
            {
                case MessageType.None:
                case MessageType.Info:
                    Debug.Log(message, context);
                    break;
                case MessageType.Warning:
                    Debug.LogWarning(message, context);
                    break;
                case MessageType.Error:
                    Debug.LogError(message, context);
                    break;
            }
        }
    }
}
