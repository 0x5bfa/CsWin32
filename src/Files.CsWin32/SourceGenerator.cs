// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Text.Json;
using Microsoft.CodeAnalysis.Text;

namespace Files.CsWin32;

/// <summary>
/// Generates the source code for the p/invoke methods and supporting types into some C# project.
/// </summary>
[Generator]
public class SourceGenerator : ISourceGenerator
{
	private const string NativeMethodsTxtAdditionalFileName = "NativeMethods.txt";
	private const string NativeMethodsJsonAdditionalFileName = "NativeMethods.json";

	private static readonly char[] ZeroWhiteSpace =
	[
		'\uFEFF', // ZERO WIDTH NO-BREAK SPACE (U+FEFF)
		'\u200B', // ZERO WIDTH SPACE (U+200B)
	];

	private static readonly JsonSerializerOptions JsonOptions = new()
	{
		AllowTrailingCommas = true,
		ReadCommentHandling = JsonCommentHandling.Skip,
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
	};

	/// <inheritdoc/>
	public void Initialize(GeneratorInitializationContext context)
	{
	}

	/// <inheritdoc/>
	public void Execute(GeneratorExecutionContext context)
	{
		if (context.Compilation is not CSharpCompilation compilation)
		{
			return;
		}

		GeneratorOptions options;
		AdditionalText? nativeMethodsJsonFile = context.AdditionalFiles
			.FirstOrDefault(af => string.Equals(Path.GetFileName(af.Path), NativeMethodsJsonAdditionalFileName, StringComparison.OrdinalIgnoreCase));
		if (nativeMethodsJsonFile is object)
		{
			string optionsJson = nativeMethodsJsonFile.GetText(context.CancellationToken)!.ToString();
			try
			{
				options = JsonSerializer.Deserialize<GeneratorOptions>(optionsJson, JsonOptions);
			}
			catch (JsonException ex)
			{
				context.ReportDiagnostic(Diagnostic.Create(DiagnosticDescriptors.OptionsParsingError, location: null, nativeMethodsJsonFile.Path, ex.Message));
				return;
			}
		}
		else
		{
			options = new GeneratorOptions();
		}

		IEnumerable<AdditionalText> nativeMethodsTxtFiles = context.AdditionalFiles
			.Where(af => string.Equals(Path.GetFileName(af.Path), NativeMethodsTxtAdditionalFileName, StringComparison.OrdinalIgnoreCase));
		if (!nativeMethodsTxtFiles.Any())
		{
			return;
		}

		var parseOptions = (CSharpParseOptions)context.ParseOptions;

		if (!compilation.Options.AllowUnsafe)
		{
			context.ReportDiagnostic(Diagnostic.Create(DiagnosticDescriptors.UnsafeCodeRequired, location: null));
		}

		if (compilation.GetTypeByMetadataName("System.Memory`1") is null)
		{
			context.ReportDiagnostic(Diagnostic.Create(DiagnosticDescriptors.MissingRecommendedReference, location: null, "System.Memory"));
		}

		IEnumerable<string> appLocalLibraries = CollectAppLocalAllowedLibraries(context);
		Docs? docs = ParseDocs(context);
		Generator[] generators = CollectMetadataPaths(context).Select(path => new Generator(path, docs, appLocalLibraries, options, compilation, parseOptions)).ToArray();
		if (TryFindNonUniqueValue(generators, g => g.InputAssemblyName, StringComparer.OrdinalIgnoreCase, out (Generator Item, string Value) nonUniqueGenerator))
		{
			context.ReportDiagnostic(Diagnostic.Create(DiagnosticDescriptors.NonUniqueMetadataInputs, null, nonUniqueGenerator.Value));
			return;
		}

		using SuperGenerator superGenerator = SuperGenerator.Combine(generators);
		foreach (AdditionalText nativeMethodsTxtFile in nativeMethodsTxtFiles)
		{
			SourceText? nativeMethodsTxt = nativeMethodsTxtFile.GetText(context.CancellationToken);
			if (nativeMethodsTxt is null)
			{
				return;
			}

			foreach (TextLine line in nativeMethodsTxt.Lines)
			{
				context.CancellationToken.ThrowIfCancellationRequested();
				string name = line.ToString();
				if (string.IsNullOrWhiteSpace(name) || name.StartsWith("//", StringComparison.InvariantCulture))
				{
					continue;
				}

				name = name.Trim().Trim(ZeroWhiteSpace);
				var location = Location.Create(nativeMethodsTxtFile.Path, line.Span, nativeMethodsTxt.Lines.GetLinePositionSpan(line.Span));
				try
				{
					if (Generator.GetBannedAPIs(options).TryGetValue(name, out string? reason))
					{
						context.ReportDiagnostic(Diagnostic.Create(DiagnosticDescriptors.BannedApi, location, reason));
						continue;
					}

					if (name.EndsWith(".*", StringComparison.Ordinal))
					{
						string? moduleName = name.Substring(0, name.Length - 2);
						int matches = superGenerator.TryGenerateAllExternMethods(moduleName, context.CancellationToken);
						switch (matches)
						{
							case 0:
								context.ReportDiagnostic(Diagnostic.Create(DiagnosticDescriptors.NoMethodsForModule, location, moduleName));
								break;
							case > 1:
								// Stop complaining about multiple metadata exporting methods from the same module.
								// https://github.com/microsoft/CsWin32/issues/1201
								////context.ReportDiagnostic(Diagnostic.Create(AmbiguousMatchError, location, moduleName));
								break;
						}

						continue;
					}

					superGenerator.TryGenerate(name, out IReadOnlyCollection<string> matchingApis, out IReadOnlyCollection<string> redirectedEnums, context.CancellationToken);
					foreach (string declaringEnum in redirectedEnums)
					{
						context.ReportDiagnostic(Diagnostic.Create(DiagnosticDescriptors.UseEnumValueDeclaringType, location, declaringEnum));
					}

					switch (matchingApis.Count)
					{
						case 0:
							ReportNoMatch(location, name);
							break;
						case > 1:
							context.ReportDiagnostic(Diagnostic.Create(DiagnosticDescriptors.AmbiguousMatchErrorWithSuggestions, location, name, ConcatSuggestions(matchingApis)));
							break;
					}
				}
				catch (GenerationFailedException ex)
				{
					if (Generator.IsPlatformCompatibleException(ex))
					{
						context.ReportDiagnostic(Diagnostic.Create(DiagnosticDescriptors.CpuArchitectureIncompatibility, location));
					}
					else
					{
						// Build up a complete error message.
						context.ReportDiagnostic(Diagnostic.Create(DiagnosticDescriptors.InternalError, location, AssembleFullExceptionMessage(ex)));
					}
				}
			}
		}

		foreach (KeyValuePair<string, CompilationUnitSyntax> unit in superGenerator.GetCompilationUnits(context.CancellationToken))
		{
			context.AddSource(unit.Key, unit.Value.GetText(Encoding.UTF8));
		}

		string ConcatSuggestions(IReadOnlyCollection<string> suggestions)
		{
			var suggestionBuilder = new StringBuilder();
			int i = 0;
			foreach (string suggestion in suggestions)
			{
				if (++i > 0)
				{
					suggestionBuilder.Append(i < suggestions.Count - 1 ? ", " : " or ");
				}

				suggestionBuilder.Append('"');
				suggestionBuilder.Append(suggestion);
				suggestionBuilder.Append('"');
			}

			return suggestionBuilder.ToString();
		}

		void ReportNoMatch(Location? location, string failedAttempt)
		{
			IReadOnlyList<string> suggestions = superGenerator.GetSuggestions(failedAttempt);
			if (suggestions.Count > 0)
			{
				context.ReportDiagnostic(Diagnostic.Create(DiagnosticDescriptors.NoMatchingMethodOrTypeWithSuggestions, location, failedAttempt, ConcatSuggestions(suggestions)));
			}
			else
			{
				context.ReportDiagnostic(Diagnostic.Create(
					Generator.ContainsIllegalCharactersForAPIName(failedAttempt) ? DiagnosticDescriptors.NoMatchingMethodOrTypeWithBadCharacters : DiagnosticDescriptors.NoMatchingMethodOrType,
					location,
					failedAttempt));
			}
		}
	}

	private static string AssembleFullExceptionMessage(Exception ex)
	{
		var sb = new StringBuilder();

		Exception? inner = ex;
		while (inner is object)
		{
			sb.Append(inner.Message);
			if (sb.Length > 0 && sb[sb.Length - 1] != '.')
			{
				sb.Append('.');
			}

			sb.Append(' ');
			inner = inner.InnerException;
		}

		sb.AppendLine();
		sb.AppendLine(ex.ToString());

		return sb.ToString();
	}

	private static bool TryFindNonUniqueValue<T, TValue>(IEnumerable<T> sequence, Func<T, TValue> valueSelector, IEqualityComparer<TValue> comparer, out (T Item, TValue Value) nonUniqueValue)
	{
		HashSet<TValue> seenValues = new(comparer);
		nonUniqueValue = default;
		foreach (T item in sequence)
		{
			TValue value = valueSelector(item);
			if (!seenValues.Add(value))
			{
				nonUniqueValue = (item, value);
				return true;
			}
		}

		return false;
	}

	private static IReadOnlyList<string> CollectMetadataPaths(GeneratorExecutionContext context)
	{
		if (!context.AnalyzerConfigOptions.GlobalOptions.TryGetValue("build_property.CsWin32InputMetadataPaths", out string? delimitedMetadataBasePaths) ||
			string.IsNullOrWhiteSpace(delimitedMetadataBasePaths))
		{
			return Array.Empty<string>();
		}

		string[] metadataBasePaths = delimitedMetadataBasePaths.Split('|');
		return metadataBasePaths;
	}

	private static IEnumerable<string> CollectAppLocalAllowedLibraries(GeneratorExecutionContext context)
	{
		if (!context.AnalyzerConfigOptions.GlobalOptions.TryGetValue("build_property.CsWin32AppLocalAllowedLibraries", out string? delimitedAppLocalLibraryPaths) ||
			string.IsNullOrWhiteSpace(delimitedAppLocalLibraryPaths))
		{
			return Array.Empty<string>();
		}

		return delimitedAppLocalLibraryPaths.Split('|').Select(Path.GetFileName);
	}

	private static Docs? ParseDocs(GeneratorExecutionContext context)
	{
		Docs? docs = null;
		if (context.AnalyzerConfigOptions.GlobalOptions.TryGetValue("build_property.CsWin32InputDocPaths", out string? delimitedApiDocsPaths) &&
			!string.IsNullOrWhiteSpace(delimitedApiDocsPaths))
		{
			string[] apiDocsPaths = delimitedApiDocsPaths!.Split('|');
			if (apiDocsPaths.Length > 0)
			{
				List<Docs> docsList = new(apiDocsPaths.Length);
				foreach (string path in apiDocsPaths)
				{
					try
					{
						docsList.Add(Docs.Get(path));
					}
					catch (Exception e)
					{
						context.ReportDiagnostic(Diagnostic.Create(DiagnosticDescriptors.DocParsingError, null, path, e));
					}
				}

				docs = Docs.Merge(docsList);
			}
		}

		return docs;
	}
}
