// Copyright (c) 0x5BFA.

namespace CsWin32Ex
{
	internal static class FeatureHelpers
	{
		// Returns the syntax of the enum identifier name if the attribute is present, otherwise null.
		internal static bool TryExtractAssociatedEnumAttribute(CustomAttributeHandleCollection customAttributeHandles, MetadataReader reader, [NotNullWhen(true)] out IdentifierNameSyntax? syntax)
		{
			syntax = null;

			if (MetadataHelpers.HasCustomAttributeOf(customAttributeHandles, reader, Generator.InteropDecorationNamespace, Generator.AssociatedEnumAttribute, out var attribute) &&
				attribute?.DecodeValue(CustomAttributeTypeProvider.Instance) is { } parameters &&
				parameters.FixedArguments[0].Value is string enumName)
			{
				syntax = SyntaxFactoryHelpers.CreateIdentifierNameSyntaxFromName(enumName);
				return true;
			}

			return false;
		}

		internal static bool HasFixedBufferAttribute(CustomAttributeHandleCollection customAttributeHandles, MetadataReader reader, out CustomAttribute? attribute)
		{
			return MetadataHelpers.HasCustomAttributeOf(customAttributeHandles, reader, Generator.SystemRuntimeCompilerServices, nameof(FixedBufferAttribute), out attribute);
		}

		// Returns the syntax of the field type and the array length if the attribute is present, otherwise null.
		internal static bool TryExtractFixedBufferAttribute(CustomAttributeHandleCollection customAttributeHandles, MetadataReader reader, [NotNullWhen(true)] out TypeSyntax? memberTypeSyntax, out int memberArrayLength)
		{
			memberTypeSyntax = null;
			memberArrayLength = -1;

			if (HasFixedBufferAttribute(customAttributeHandles, reader, out var attribute) &&
				attribute?.DecodeValue(CustomAttributeTypeProvider.Instance) is { } parameters &&
				parameters.FixedArguments[0].Value is TypeSyntax typeSyntax &&
				parameters.FixedArguments[1].Value is int arrayLength)
			{
				memberTypeSyntax = typeSyntax;
				memberArrayLength = arrayLength;
				return true;
			}

			return false;
		}

		internal static bool HasObsoleteAttribute(CustomAttributeHandleCollection customAttributeHandles, MetadataReader reader, out CustomAttribute? attribute)
		{
			return MetadataHelpers.HasCustomAttributeOf(customAttributeHandles, reader, nameof(System), nameof(ObsoleteAttribute), out attribute);
		}

		internal static bool TryExtractNativeBitfieldAttributes(CustomAttributeHandleCollection customAttributeHandles, MetadataReader reader, [NotNullWhen(true)] out List<CustomAttribute>? attributes)
		{
			attributes = null;

			foreach (var customAttributeHandle in customAttributeHandles)
			{
				var attribute = reader.GetCustomAttribute(customAttributeHandle);
				if (MetadataHelpers.IsAttribute(attribute, reader, Generator.InteropDecorationNamespace, Generator.NativeBitfieldAttribute))
				{
					attributes ??= [];
					attributes.Add(attribute);
				}
			}

#pragma warning disable CS8762
			return attributes is not null && attributes.Count is not 0;
#pragma warning restore CS8762
		}
	}
}
