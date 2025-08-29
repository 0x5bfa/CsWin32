// Copyright (c) 0x5BFA.

namespace CsWin32Ex;

internal static class WinMDFileHelper
{
	[Flags]
	internal enum InteropArchitecture
	{
#pragma warning disable SA1602 // Enumeration items should be documented
		None = 0x0,
		X86 = 0x1,
		X64 = 0x2,
		Arm64 = 0x4,
		All = 0x7,
#pragma warning restore SA1602 // Enumeration items should be documented
	}

	internal static bool IsCompatibleWithPlatform(MetadataReader reader, WinMDFileIndexer index, Platform? platform, CustomAttributeHandleCollection customAttributesOnMember)
	{
		// This metadata never uses the SupportedArchitectureAttribute, so we assume this member is compatible.
		if (index.SupportedArchitectureAttributeCtor == default)
			return true;

		// Without a compilation, we cannot check the platform compatibility.
		if (platform is null)
			return false;

		foreach (CustomAttributeHandle attributeHandle in customAttributesOnMember)
		{
			CustomAttribute attribute = reader.GetCustomAttribute(attributeHandle);
			if (attribute.Constructor.Equals(index.SupportedArchitectureAttributeCtor))
			{
				var requiredPlatform = (InteropArchitecture)(int)attribute.DecodeValue(CustomAttributeTypeProvider.Instance).FixedArguments[0].Value!;
				return platform switch
				{
					Platform.AnyCpu or Platform.AnyCpu32BitPreferred => requiredPlatform is InteropArchitecture.All,
					Platform.Arm64 => (requiredPlatform & InteropArchitecture.Arm64) is InteropArchitecture.Arm64,
					Platform.X86 => (requiredPlatform & InteropArchitecture.X86) is InteropArchitecture.X86,
					Platform.X64 => (requiredPlatform & InteropArchitecture.X64) is InteropArchitecture.X64,
					_ => false,
				};
			}
		}

		// No SupportedArchitectureAttribute on this member, so assume it is compatible.
		return true;
	}

	internal static bool IsAttribute(MetadataReader reader, CustomAttribute attribute, string ns, string name)
	{
		StringHandle actualNamespace, actualName;

		if (attribute.Constructor.Kind is HandleKind.MemberReference)
		{
			MemberReference memberReference = reader.GetMemberReference((MemberReferenceHandle)attribute.Constructor);
			TypeReference parentRef = reader.GetTypeReference((TypeReferenceHandle)memberReference.Parent);
			actualNamespace = parentRef.Namespace;
			actualName = parentRef.Name;
		}
		else if (attribute.Constructor.Kind is HandleKind.MethodDefinition)
		{
			MethodDefinition methodDef = reader.GetMethodDefinition((MethodDefinitionHandle)attribute.Constructor);
			TypeDefinition typeDef = reader.GetTypeDefinition(methodDef.GetDeclaringType());
			actualNamespace = typeDef.Namespace;
			actualName = typeDef.Name;
		}
		else
		{
			throw new NotSupportedException("Unsupported attribute constructor kind: " + attribute.Constructor.Kind);
		}

		return reader.StringComparer.Equals(actualName, name) && reader.StringComparer.Equals(actualNamespace, ns);
	}

	internal static CustomAttribute? TryGetAttributeOn(MetadataReader reader, CustomAttributeHandleCollection? customAttributeHandles, string attributeNamespace, string attributeName)
	{
		if (customAttributeHandles is not null)
		{
			foreach (CustomAttributeHandle handle in customAttributeHandles)
			{
				CustomAttribute att = reader.GetCustomAttribute(handle);
				if (IsAttribute(reader, att, attributeNamespace, attributeName))
				{
					return att;
				}
			}
		}

		return null;
	}

	internal static IEnumerable<CustomAttribute> FindAttributes(MetadataReader reader, CustomAttributeHandleCollection? customAttributeHandles, string attributeNamespace, string attributeName)
	{
		if (customAttributeHandles is not null)
		{
			foreach (CustomAttributeHandle handle in customAttributeHandles)
			{
				CustomAttribute att = reader.GetCustomAttribute(handle);
				if (IsAttribute(reader, att, attributeNamespace, attributeName))
				{
					yield return att;
				}
			}
		}
	}
}
