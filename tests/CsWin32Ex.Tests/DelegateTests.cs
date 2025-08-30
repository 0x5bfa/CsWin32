// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

public class DelegateTests : GeneratorTestBase
{
	public DelegateTests(ITestOutputHelper logger) : base(logger) { }

	[Theory, CombinatorialData]
	public void DelegateWithPointerParameters([CombinatorialValues("LPD3DHAL_RENDERSTATECB")] string delegateName, bool allowMarshaling)
	{
		// Create a generator that allows or disallows marshaling
		var generator = CreateGenerator(DefaultTestGeneratorOptions with { AllowMarshaling = allowMarshaling });

		// The delegate should be generated without diagnostics
		Assert.True(generator.TryGenerate(delegateName, out _, CancellationToken.None));
		AssertNoDiagnostics();
		CollectGeneratedCode(generator);

		var matchedSyntaxes = compilation.SyntaxTrees.SelectMany(st => st.GetRoot().DescendantNodes()
			.OfType<DelegateDeclarationSyntax>())
			.Where(x => x.Identifier.ValueText == delegateName);

		if (allowMarshaling)
		{
			Assert.NotEmpty(matchedSyntaxes);

			// The parameter should have the ref keyword
			Assert.True(matchedSyntaxes.Single().ParameterList.Parameters.ElementAt(0).Modifiers.ElementAt(0) is SyntaxToken refSyntaxToken);
			Assert.True(refSyntaxToken.Kind() is SyntaxKind.RefKeyword);
		}
		else
		{
			// Delegates are not allowed when marshaling is disallowed
			Assert.Empty(matchedSyntaxes);
		}
	}

	[Theory, CombinatorialData]
	public void DelegateWithUntyped([CombinatorialValues("PROC", "FARPROC")] string delegateName)
	{
		GenerateApi(delegateName);

		// The generated syntax shall be a struct for an untyped delegate
		var declarationSyntax = Assert.IsType<StructDeclarationSyntax>(FindGeneratedType(delegateName).Single());

		// The generated syntax shall have a method called CreateDelegate with a generic type TDelegate
		Assert.Contains(
			declarationSyntax.Members,
			member =>
				member is MethodDeclarationSyntax methodDeclarationSyntax &&
				methodDeclarationSyntax.Identifier.Text == "CreateDelegate" &&
				methodDeclarationSyntax.TypeParameterList!.Parameters.ElementAt(0) is TypeParameterSyntax typeParameterSyntax &&
				typeParameterSyntax.Identifier.Text == "TDelegate");
	}
}
