// Copyright (c) 0x5BFA.

using System.Reflection.PortableExecutable;

namespace CsWin32Ex;

internal class WinMDReaderRental : IDisposable
{
	private (PEReader PEReader, MetadataReader MDReader, WinMDFile File)? _state;

	internal MetadataReader Value
		=> _state?.MDReader ?? throw new ObjectDisposedException(typeof(WinMDReaderRental).FullName);

	internal WinMDReaderRental(PEReader peReader, MetadataReader mdReader, WinMDFile file)
	{
		_state = (peReader, mdReader, file);
	}

	public void Dispose()
	{
		if (_state is (PEReader peReader, MetadataReader mdReader, WinMDFile file))
		{
			file.ReturnWinMDReader(peReader, mdReader);
			_state = null;
		}
	}
}
