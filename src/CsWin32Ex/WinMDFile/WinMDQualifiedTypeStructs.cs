// Copyright (c) 0x5BFA.

namespace CsWin32Ex;

internal record struct QualifiedTypeReferenceHandle(Generator Generator, TypeReferenceHandle ReferenceHandle)
{
	internal MetadataReader Reader => this.Generator.WinMDReader;

	internal QualifiedTypeReference Resolve() => new(this.Generator, this.Generator.WinMDReader.GetTypeReference(this.ReferenceHandle));
}

internal record struct QualifiedTypeReference(Generator Generator, TypeReference Reference)
{
	internal MetadataReader Reader => this.Generator.WinMDReader;
}

internal record struct QualifiedTypeDefinitionHandle(Generator Generator, TypeDefinitionHandle DefinitionHandle)
{
	internal MetadataReader Reader => this.Generator.WinMDReader;

	internal QualifiedTypeDefinition Resolve() => new(this.Generator, this.Generator.WinMDReader.GetTypeDefinition(this.DefinitionHandle));
}

internal record struct QualifiedTypeDefinition(Generator Generator, TypeDefinition Definition)
{
	internal MetadataReader Reader => this.Generator.WinMDReader;
}

internal record struct QualifiedMethodDefinitionHandle(Generator Generator, MethodDefinitionHandle MethodHandle)
{
	internal MetadataReader Reader => this.Generator.WinMDReader;

	internal QualifiedMethodDefinition Resolve() => new(this.Generator, this.Generator.WinMDReader.GetMethodDefinition(this.MethodHandle));
}

internal record struct QualifiedMethodDefinition(Generator Generator, MethodDefinition Method)
{
	internal MetadataReader Reader => this.Generator.WinMDReader;
}
