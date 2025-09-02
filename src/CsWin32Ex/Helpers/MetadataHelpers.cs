// Copyright (c) 0x5BFA.

namespace CsWin32Ex
{
	internal static class MetadataHelpers
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

		internal static bool IsAttribute(CustomAttribute attribute, MetadataReader reader, string namespaceName, string attributeName)
		{
			StringHandle actualNamespaceName, actualName;

			if (attribute.Constructor.Kind is HandleKind.MemberReference)
			{
				MemberReference memberReference = reader.GetMemberReference((MemberReferenceHandle)attribute.Constructor);
				TypeReference parentRef = reader.GetTypeReference((TypeReferenceHandle)memberReference.Parent);
				actualNamespaceName = parentRef.Namespace;
				actualName = parentRef.Name;
			}
			else if (attribute.Constructor.Kind is HandleKind.MethodDefinition)
			{
				MethodDefinition methodDef = reader.GetMethodDefinition((MethodDefinitionHandle)attribute.Constructor);
				TypeDefinition typeDef = reader.GetTypeDefinition(methodDef.GetDeclaringType());
				actualNamespaceName = typeDef.Namespace;
				actualName = typeDef.Name;
			}
			else
			{
				throw new NotSupportedException("Unsupported attribute constructor kind: " + attribute.Constructor.Kind);
			}

			return reader.StringComparer.Equals(actualName, attributeName) && reader.StringComparer.Equals(actualNamespaceName, namespaceName);
		}

		internal static (byte? LengthInBits, bool Signed) GetLengthInBits(PrimitiveTypeCode code)
		{
			return code switch
			{
				PrimitiveTypeCode.Byte =>		(8,		false),
				PrimitiveTypeCode.SByte =>		(8,		true),
				PrimitiveTypeCode.UInt16 =>		(16,	false),
				PrimitiveTypeCode.Int16 =>		(16,	true),
				PrimitiveTypeCode.UInt32 =>		(32,	false),
				PrimitiveTypeCode.Int32 =>		(32,	true),
				PrimitiveTypeCode.UInt64 =>		(64,	false),
				PrimitiveTypeCode.Int64 =>		(64,	true),
				PrimitiveTypeCode.UIntPtr =>	(null,	false),
				PrimitiveTypeCode.IntPtr =>		(null, true),
				_ => throw new NotImplementedException(),
			};
		}

		internal static byte GetLengthInBytes(PrimitiveTypeCode code) => code switch
		{
			PrimitiveTypeCode.SByte or PrimitiveTypeCode.Byte => 1,
			PrimitiveTypeCode.Int16 or PrimitiveTypeCode.UInt16 => 2,
			PrimitiveTypeCode.Int32 or PrimitiveTypeCode.UInt32 => 4,
			PrimitiveTypeCode.Int64 or PrimitiveTypeCode.UInt64 => 8,
			PrimitiveTypeCode.IntPtr or PrimitiveTypeCode.UIntPtr => 8, // Assume this -- guessing high isn't a problem for our use case.
			_ => throw new NotSupportedException($"Unsupported primitive type code: {code}"),
		};
	}
}
