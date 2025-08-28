// Copyright (c) 0x5BFA.

namespace Files.CsWin32
{
	internal enum GeneratingElement
	{
		/// <summary>
		/// Any other member that isn't otherwise enumerated.
		/// </summary>
		Other,

		/// <summary>
		/// A member on a COM interface that is actually being generated as an interface (as opposed to a struct for no-marshal COM).
		/// </summary>
		InterfaceMember,

		/// <summary>
		/// A member on a COM interface that is declared as a struct instead of an interface to avoid the marshaler.
		/// </summary>
		InterfaceAsStructMember,

		/// <summary>
		/// A delegate.
		/// </summary>
		Delegate,

		/// <summary>
		/// An extern, static method.
		/// </summary>
		ExternMethod,

		/// <summary>
		/// A property on a COM interface or struct.
		/// </summary>
		Property,

		/// <summary>
		/// A field on a struct.
		/// </summary>
		Field,

		/// <summary>
		/// A constant value.
		/// </summary>
		Constant,

		/// <summary>
		/// A function pointer.
		/// </summary>
		FunctionPointer,

		/// <summary>
		/// An enum value.
		/// </summary>
		EnumValue,

		/// <summary>
		/// A friendly overload.
		/// </summary>
		FriendlyOverload,

		/// <summary>
		/// A member on a helper class (e.g. a SafeHandle-derived class).
		/// </summary>
		HelperClassMember,

		/// <summary>
		/// A member of a struct that does <em>not</em> stand for a COM interface.
		/// </summary>
		StructMember,
	}

	internal enum Feature
	{
		/// <summary>
		/// Indicates that interfaces can declare static members. This requires at least .NET 7 and C# 11.
		/// </summary>
		InterfaceStaticMembers,
	}
}
