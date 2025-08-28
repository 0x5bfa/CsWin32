// Copyright (c) 0x5BFA.

namespace CsWin32Ex;

/// <summary>
/// An exception thrown when code generation fails.
/// </summary>
[Serializable]
public class GenerationFailedException : Exception
{
	/// <summary>
	/// Initializes a new instance of the <see cref="GenerationFailedException"/> class.
	/// </summary>
	public GenerationFailedException()
	{
	}

	/// <summary>
	/// Initializes a new instance of the <see cref="GenerationFailedException"/> class.
	/// </summary>
	/// <inheritdoc cref="Exception(string)" />
	public GenerationFailedException(string message)
		: base(message)
	{
	}

	/// <summary>
	/// Initializes a new instance of the <see cref="GenerationFailedException"/> class.
	/// </summary>
	/// <inheritdoc cref="Exception(string, Exception)" />
	public GenerationFailedException(string message, Exception inner)
		: base(message, inner)
	{
	}

	/// <summary>
	/// Initializes a new instance of the <see cref="GenerationFailedException"/> class.
	/// </summary>
	/// <inheritdoc cref="Exception(System.Runtime.Serialization.SerializationInfo, System.Runtime.Serialization.StreamingContext)" />
	protected GenerationFailedException(
	  System.Runtime.Serialization.SerializationInfo info,
	  System.Runtime.Serialization.StreamingContext context)
		: base(info, context)
	{
	}
}
