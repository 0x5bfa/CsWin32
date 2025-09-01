// Copyright (c) 0x5BFA.

namespace CsWin32Ex
{
	internal static class StructHelpers
	{
		internal static bool HasTypeAttributesOf(TypeDefinition typeDefinition, TypeAttributes typeAttributes)
		{
			return typeDefinition.Attributes.HasFlag(typeAttributes);
		}

		internal static bool HasFieldAttributesOf(FieldDefinition fieldDefinition, FieldAttributes fieldAttributes)
		{
			return fieldDefinition.Attributes.HasFlag(fieldAttributes);
		}

		internal static bool HasCustomAttributeOf(CustomAttributeHandleCollection customAttributeHandles, MetadataReader reader, string namespaceName, string attributeName, out CustomAttribute? customAttribute)
		{
			customAttribute = null;

			foreach (CustomAttributeHandle customAttributeHandle in customAttributeHandles)
			{
				var actualCustomAttribute = reader.GetCustomAttribute(customAttributeHandle);

				StringHandle actualNamespaceName, actualAttributeName;

				if (actualCustomAttribute.Constructor.Kind is HandleKind.MemberReference)
				{
					var memberReference = reader.GetMemberReference((MemberReferenceHandle)actualCustomAttribute.Constructor!);
					var parentTypeReference = reader.GetTypeReference((TypeReferenceHandle)memberReference.Parent);
					actualNamespaceName = parentTypeReference.Namespace;
					actualAttributeName = parentTypeReference.Name;
				}
				else if (actualCustomAttribute.Constructor.Kind is HandleKind.MethodDefinition)
				{
					var methodDefinition = reader.GetMethodDefinition((MethodDefinitionHandle)actualCustomAttribute.Constructor!);
					var methodTypeDefinition = reader.GetTypeDefinition(methodDefinition.GetDeclaringType());
					actualNamespaceName = methodTypeDefinition.Namespace;
					actualAttributeName = methodTypeDefinition.Name;
				}
				else
				{
					throw new NotSupportedException($"Unsupported attribute constructor kind: \"{actualCustomAttribute.Constructor.Kind}\"");
				}

				if (reader.StringComparer.Equals(actualNamespaceName, namespaceName) && reader.StringComparer.Equals(actualAttributeName, attributeName))
				{
					customAttribute = actualCustomAttribute;
					return true;
				}
			}

			return false;
		}

		internal static bool IsConstField(FieldDefinition fieldDefinition)
		{
			// ".field <modifiers> static literal <type> <name>"
			return fieldDefinition.Attributes.HasFlag(FieldAttributes.Static) && fieldDefinition.Attributes.HasFlag(FieldAttributes.Literal);
		}

		internal static bool IsStaticField(FieldDefinition fieldDefinition)
		{
			// ".field <modifiers> static <type> <name>"
			return fieldDefinition.Attributes.HasFlag(FieldAttributes.Static) && !fieldDefinition.Attributes.HasFlag(FieldAttributes.Literal);
		}

		internal static bool TryGetFlexibleArrayField(TypeDefinition typeDefinition, MetadataReader reader, [NotNullWhen(true)] out FieldDefinitionHandle? flexibleArrayFieldDefinitionHandle)
		{
			flexibleArrayFieldDefinitionHandle = null;

			if (typeDefinition.GetFields().LastOrDefault() is FieldDefinitionHandle { IsNil: false } lastFieldDefinitionHandle)
			{
				var lastField = reader.GetFieldDefinition(lastFieldDefinitionHandle);
				if (WinMDFileHelper.TryGetAttributeOn(reader, lastField.GetCustomAttributes(), Generator.InteropDecorationNamespace, Generator.FlexibleArrayAttribute) is not null)
				{
					flexibleArrayFieldDefinitionHandle = lastFieldDefinitionHandle;
					return true;
				}
			}

			return false;
		}
	}
}
