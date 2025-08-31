// Copyright (c) 0x5BFA.

namespace CsWin32Ex
{
	/// <summary>
	/// Builds a mutable XML comments.
	/// </summary>
	internal class XmlCommentBuilder
	{
		private readonly StringBuilder _builder = new();
		private readonly string[] _elementsToTruncate;

		/// <summary>Initializes an instance of <see cref="XmlCommentBuilder"/>.</summary>
		/// <param name="elementsToTruncate">An array of XML elements that, if encountered in the text, will cause truncation of the rest of the text.</param>
		internal XmlCommentBuilder(string[]? elementsToTruncate = null)
		{
			_elementsToTruncate = elementsToTruncate ?? [ "<table", "<img", "<ul", "<ol", "```", "<<" ];
		}

		internal XmlCommentBuilder StartSummaryElement()
		{
			_builder.AppendLine("/// <summary>");
			return this;
		}

		internal XmlCommentBuilder EndSummaryElement()
		{
			_builder.AppendLine("/// </summary>");
			return this;
		}

		internal XmlCommentBuilder StartParamElement(string parameterName)
		{
			_builder.AppendLine($"/// </param name=\"{parameterName}\">");
			return this;
		}

		internal XmlCommentBuilder EndParamElement()
		{
			_builder.AppendLine("/// </param>");
			return this;
		}

		internal XmlCommentBuilder StartReturnsElement()
		{
			_builder.AppendLine("/// <returns>");
			return this;
		}

		internal XmlCommentBuilder EndReturnsElement()
		{
			_builder.AppendLine("/// </returns>");
			return this;
		}

		internal XmlCommentBuilder StartRemarksElement()
		{
			_builder.AppendLine("/// <remarks>");
			return this;
		}

		internal XmlCommentBuilder EndRemarksElement()
		{
			_builder.AppendLine("/// </remarks>");
			return this;
		}

		internal XmlCommentBuilder StartParaElement()
		{
			_builder.AppendLine("/// <para>");
			return this;
		}

		internal XmlCommentBuilder EndParaElement()
		{
			_builder.AppendLine($"/// </para>");
			return this;
		}

		internal XmlCommentBuilder AppendLine(string line)
		{
			_builder.AppendLine($"/// {line.Trim()}");
			return this;
		}

		internal XmlCommentBuilder AppendParagraphs(string text)
		{
			bool isTruncated = false;

			var paragraphs = text.Split([ "\r\n\r\n", "\n\n" ], StringSplitOptions.RemoveEmptyEntries);
			foreach (var paragraph in paragraphs)
			{
				StartParaElement();

				var lines = paragraph.Split([ "\r\n", "\n" ], StringSplitOptions.RemoveEmptyEntries);
				foreach (var line in lines)
				{
					// If we encounter something that looks like a code block or other complex structure, truncate the rest
					if (HasReasonToTruncateRest(text))
					{
						AppendLine("...");
						isTruncated = true;
						break;
					}

					AppendLine(line);
				}

				EndParaElement();

				if (isTruncated)
					break;
			}

			return this;
		}

		internal XmlCommentBuilder AppendToLearnMoreVisitLinkText(Uri? url, string? anchor = null, string? text = "Read more on learn.microsoft.com")
		{
			if (string.IsNullOrWhiteSpace(anchor))
				AppendParagraphs($"<see href=\"{url}\">{text}</see>.");
			else
				AppendParagraphs($"<see href=\"{url}#{anchor}\">{text}</see>.");

			return this;
		}

		internal XmlCommentBuilder Clear()
		{
			_builder.Clear();
			return this;
		}

		private bool HasReasonToTruncateRest(string text)
		{
			foreach (var element in _elementsToTruncate)
			{
				if (text.IndexOf(element, StringComparison.OrdinalIgnoreCase) >= 0)
					return true;
			}

			return false;
		}

		/// <inheritdoc cref="ToString" />
		public override string ToString()
		{
			return _builder.ToString();
		}
	}
}
