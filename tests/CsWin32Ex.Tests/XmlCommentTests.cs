// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

public class XmlCommentTests : GeneratorTestBase
{
	public XmlCommentTests(ITestOutputHelper logger) : base(logger)
	{
	}

	[Fact]
	public void ExternWithXmlComment()
	{
		// Generates an extern with the friendly overloads disabled and with xml comments
		var generator = CreateGenerator(DefaultTestGeneratorOptions with { FriendlyOverloads = new() { Enabled = false } }, null, true);
		Assert.True(generator.TryGenerate("CreateFile", out _, CancellationToken.None));
		AssertNoDiagnostics();
		CollectGeneratedCode(generator);

		var declarationSyntax = compilation.SyntaxTrees.SelectMany(st => st.GetRoot().DescendantNodes()
			.OfType<MethodDeclarationSyntax>())
			.Where(x => x.Identifier.ValueText == "CreateFile");
		Assert.NotEmpty(declarationSyntax);

		// The syntax should have a XML comment
		Assert.Contains(declarationSyntax.Single().GetLeadingTrivia(), trivia => trivia.GetStructure() is DocumentationCommentTriviaSyntax);

		string leadingTrivia = declarationSyntax.Single().ToFullString();
	}

	[Fact]
	public void StructWithXmlComment()
	{
		// Generates an extern with the friendly overloads disabled and with xml comments
		var generator = CreateGenerator(DefaultTestGeneratorOptions with { FriendlyOverloads = new() { Enabled = false } }, null, true);
		Assert.True(generator.TryGenerate("ACCESS_ALLOWED_ACE", out _, CancellationToken.None));
		AssertNoDiagnostics();
		CollectGeneratedCode(generator);

		var declarationSyntax = compilation.SyntaxTrees.SelectMany(st => st.GetRoot().DescendantNodes()
			.OfType<StructDeclarationSyntax>())
			.Where(x => x.Identifier.ValueText == "ACCESS_ALLOWED_ACE");
		Assert.NotEmpty(declarationSyntax);

		// The syntax should have a XML comment
		Assert.Contains(declarationSyntax.Single().GetLeadingTrivia(), trivia => trivia.GetStructure() is DocumentationCommentTriviaSyntax);
	}

	[Fact]
	public void EnumWithXmlComment()
	{
		// Generates an extern with the friendly overloads disabled and with xml comments
		var generator = CreateGenerator(DefaultTestGeneratorOptions with { FriendlyOverloads = new() { Enabled = false } }, null, true);
		Assert.True(generator.TryGenerate("HANDLE_OPTIONS", out _, CancellationToken.None));
		AssertNoDiagnostics();
		CollectGeneratedCode(generator);

		var declarationSyntax = compilation.SyntaxTrees.SelectMany(st => st.GetRoot().DescendantNodes()
			.OfType<EnumDeclarationSyntax>())
			.Where(x => x.Identifier.ValueText == "HANDLE_OPTIONS");
		Assert.NotEmpty(declarationSyntax);

		// The syntax should have a XML comment
		Assert.Contains(declarationSyntax.Single().GetLeadingTrivia(), trivia => trivia.GetStructure() is DocumentationCommentTriviaSyntax);
	}

	[Fact]
	public void ConstantWithXmlComment()
	{
		// Generates an extern with the friendly overloads disabled and with xml comments
		var generator = CreateGenerator(DefaultTestGeneratorOptions with { FriendlyOverloads = new() { Enabled = false } }, null, true);
		Assert.True(generator.TryGenerate("WM_APP", out _, CancellationToken.None));
		AssertNoDiagnostics();
		CollectGeneratedCode(generator);

		var declarationSyntax = compilation.SyntaxTrees.SelectMany(st => st.GetRoot().DescendantNodes()
			.OfType<FieldDeclarationSyntax>())
			.Where(x => x.Declaration.Variables[0].Identifier.ValueText == "WM_APP");
		Assert.NotEmpty(declarationSyntax);

		var full = declarationSyntax.Single().ToFullString();

		// The syntax should have a XML comment
		Assert.Contains(declarationSyntax.Single().GetLeadingTrivia(), trivia => trivia.GetStructure() is DocumentationCommentTriviaSyntax);
	}

	[Fact]
	public void ComInterfaceWithXmlComment()
	{
		// Generates an extern with the friendly overloads disabled and with xml comments
		var generator = CreateGenerator(DefaultTestGeneratorOptions with { FriendlyOverloads = new() { Enabled = false } }, null, true);
		Assert.True(generator.TryGenerate("IShellItem", out _, CancellationToken.None));
		AssertNoDiagnostics();
		CollectGeneratedCode(generator);

		var declarationSyntax = compilation.SyntaxTrees.SelectMany(st => st.GetRoot().DescendantNodes()
			.OfType<InterfaceDeclarationSyntax>())
			.Where(x => x.Identifier.ValueText == "IShellItem");
		Assert.NotEmpty(declarationSyntax);

		// The syntax should have a XML comment
		Assert.Contains(declarationSyntax.Single().Members[0].GetLeadingTrivia(), trivia => trivia.GetStructure() is DocumentationCommentTriviaSyntax);

		string leadingTrivia = declarationSyntax.Single().ToFullString();
	}
}
