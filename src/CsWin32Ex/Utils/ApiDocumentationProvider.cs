// Copyright (c) 0x5BFA.

using MessagePack;
using MessagePack.Formatters;
using MessagePack.Resolvers;

namespace CsWin32Ex;

/// <summary>
/// An in-memory representation of API documentation.
/// </summary>
public class ApiDocumentationProvider
{
	private static readonly Dictionary<string, ApiDocumentationProvider> DocsByPath = new Dictionary<string, ApiDocumentationProvider>(StringComparer.OrdinalIgnoreCase);
	private static readonly MessagePackSerializerOptions MsgPackOptions = MessagePackSerializerOptions.Standard.WithResolver(CompositeResolver.Create([new ApiDetailsFormatter()], [StandardResolver.Instance]));

	private readonly Dictionary<string, ApiDetails> apisAndDocs;

	private ApiDocumentationProvider(Dictionary<string, ApiDetails> apisAndDocs)
	{
		this.apisAndDocs = apisAndDocs;
	}

	/// <summary>
	/// Loads docs from a file.
	/// </summary>
	/// <param name="docsPath">The messagepack docs file to read from.</param>
	/// <returns>An instance of <see cref="ApiDocumentationProvider"/> that accesses the documentation in the file specified by <paramref name="docsPath"/>.</returns>
	public static ApiDocumentationProvider Get(string docsPath)
	{
		lock (DocsByPath)
		{
			if (DocsByPath.TryGetValue(docsPath, out ApiDocumentationProvider? existing))
			{
				return existing;
			}
		}

		using FileStream docsStream = File.OpenRead(docsPath);
		Dictionary<string, ApiDetails>? data = MessagePackSerializer.Deserialize<Dictionary<string, ApiDetails>>(docsStream, MsgPackOptions);
		var docs = new ApiDocumentationProvider(data);

		lock (DocsByPath)
		{
			if (DocsByPath.TryGetValue(docsPath, out ApiDocumentationProvider? existing))
			{
				return existing;
			}

			DocsByPath.Add(docsPath, docs);
			return docs;
		}
	}

	/// <summary>
	/// Returns a <see cref="ApiDocumentationProvider"/> instance that contains all the merged documentation from a list of docs.
	/// </summary>
	/// <param name="docs">The docs to be merged. When API documentation is provided by multiple docs in this list, the first one appearing in this list is taken.</param>
	/// <returns>An instance that contains all the docs provided. When <paramref name="docs"/> contains exactly one element, that element is returned.</returns>
	public static ApiDocumentationProvider Merge(IReadOnlyList<ApiDocumentationProvider> docs)
	{
		if (docs is null)
		{
			throw new ArgumentNullException(nameof(docs));
		}

		if (docs.Count == 1)
		{
			// Nothing to merge.
			return docs[0];
		}

		Dictionary<string, ApiDetails> mergedDocs = new(docs.Sum(d => d.apisAndDocs.Count), StringComparer.OrdinalIgnoreCase);
		foreach (ApiDocumentationProvider doc in docs)
		{
			foreach (KeyValuePair<string, ApiDetails> api in doc.apisAndDocs)
			{
				// We want a first one wins policy.
				if (!mergedDocs.ContainsKey(api.Key))
				{
					mergedDocs.Add(api.Key, api.Value);
				}
			}
		}

		return new ApiDocumentationProvider(mergedDocs);
	}

	internal bool TryGetApiDocs(string apiName, [NotNullWhen(true)] out ApiDetails? docs) => this.apisAndDocs.TryGetValue(apiName, out docs);

	/// <summary>
	/// Formatter for <see cref="ApiDetails"/>.
	/// </summary>
	/// <remarks>
	/// We have to manually write this to avoid using the <see cref="DynamicObjectResolver"/> since newer C# compiler versions fail
	/// when that dynamic type creator creates a non-collectible assembly that our own evidently collectible assembly references.
	/// </remarks>
	private class ApiDetailsFormatter : IMessagePackFormatter<ApiDetails>
	{
		public ApiDetails Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
		{
			string? helpLink = null;
			string? description = null;
			string? remarks = null;
			Dictionary<string, string>? parameters = null;
			Dictionary<string, string>? fields = null;
			string? returnValue = null;
			int count = reader.ReadArrayHeader();
			for (int i = 0; i < count; i++)
			{
				switch (i)
				{
					case 0:
						helpLink = reader.ReadString();
						break;
					case 1:
						description = reader.ReadString();
						break;
					case 2:
						remarks = reader.ReadString();
						break;
					case 3:
						parameters = options.Resolver.GetFormatterWithVerify<Dictionary<string, string>>().Deserialize(ref reader, options);
						break;
					case 4:
						fields = options.Resolver.GetFormatterWithVerify<Dictionary<string, string>>().Deserialize(ref reader, options);
						break;
					case 5:
						returnValue = reader.ReadString();
						break;
					default:
						reader.Skip();
						break;
				}
			}

			return new ApiDetails
			{
				HelpLink = helpLink is null ? null : new Uri(helpLink),
				Description = description,
				Remarks = remarks,
				Parameters = parameters ?? new Dictionary<string, string>(),
				Fields = fields ?? new Dictionary<string, string>(),
				ReturnValue = returnValue,
			};
		}

		public void Serialize(ref MessagePackWriter writer, ApiDetails value, MessagePackSerializerOptions options)
		{
			throw new NotImplementedException();
		}
	}
}
