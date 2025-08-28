// Copyright (c) 0x5BFA.

using System.IO.MemoryMappedFiles;
using System.Reflection.PortableExecutable;

namespace CsWin32Ex;

[DebuggerDisplay($"{{{nameof(Path)} ({nameof(_lastWriteTimeUtc)}),nq}}")]
internal class WinMDFile : IDisposable
{
	private static readonly Dictionary<string, WinMDFile> _cachedWinMDFiles = new(StringComparer.OrdinalIgnoreCase);

	private readonly object _lock = new();
	private readonly Stack<(PEReader PEReader, MetadataReader MDReader)> _peReaders = new();
	private readonly Dictionary<Platform?, MetadataIndex> _indexes = [];
	private readonly MemoryMappedFile _memoryMappedFile;

	internal DateTime _lastWriteTimeUtc;
	private int _readersRentedOutCount;
	private bool _disposed;

	internal string Path { get; }

	private WinMDFile(string fullPathToWinMD)
	{
		Path = fullPathToWinMD;
		_lastWriteTimeUtc = File.GetLastWriteTimeUtc(fullPathToWinMD);

		// When using FileShare.Delete, the OS will allow the file to be deleted, but it does not disrupt
		// our ability to read the file while our handle is open.
		// The file may be recreated on disk as well, and we'll keep reading the original file until we close that handle.
		// We may also open the new file while holding the old handle,
		// at which point we have handles open to both versions of the file concurrently.
		FileStream metadataStream = new(fullPathToWinMD, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
		_memoryMappedFile = MemoryMappedFile.CreateFromFile(metadataStream, mapName: null, capacity: 0, MemoryMappedFileAccess.Read, HandleInheritability.None, leaveOpen: false);
	}

	internal static WinMDFile Create(string fullPathToWinMD)
	{
		lock (_cachedWinMDFiles)
		{
			if (_cachedWinMDFiles.TryGetValue(fullPathToWinMD, out WinMDFile? metadataFile))
			{
				// We already have the file, and it is still current. Happy path.
				if (metadataFile._lastWriteTimeUtc == File.GetLastWriteTimeUtc(fullPathToWinMD))
					return metadataFile;

				// Stale file. Evict from the cache.
				_cachedWinMDFiles.Remove(fullPathToWinMD);
				metadataFile.Dispose();
			}

			// New or updated file. Re-open.
			_cachedWinMDFiles.Add(fullPathToWinMD, metadataFile = new WinMDFile(fullPathToWinMD));
			return metadataFile;
		}
	}

	internal WinMDReaderRental RentWinMDReader()
	{
		lock (_lock)
		{
			if (_disposed)
				throw new InvalidOperationException("This _memoryMappedFile was deleted and should no longer be used.");

			PEReader peReader;
			MetadataReader metadataReader;
			if (_peReaders.Count > 0)
			{
				(peReader, metadataReader) = _peReaders.Pop();
			}
			else
			{
				peReader = new(_memoryMappedFile.CreateViewStream(offset: 0, size: 0, MemoryMappedFileAccess.Read));
				metadataReader = peReader.GetMetadataReader();
			}

			_readersRentedOutCount++;
			return new WinMDReaderRental(peReader, metadataReader, this);
		}
	}

	internal MetadataIndex GetWinMDIndex(Platform? platform)
	{
		lock (_lock)
		{
			if (!_indexes.TryGetValue(platform, out MetadataIndex? index))
				_indexes.Add(platform, index = new MetadataIndex(this, platform));

			return index;
		}
	}

	private void ReturnWinMDReader(PEReader peReader, MetadataReader mdReader)
	{
		lock (_lock)
		{
			_readersRentedOutCount--;
			Debug.Assert(_readersRentedOutCount >= 0, "Some reader was returned more than once.");

			if (_disposed)
			{
				// This file has been marked as stale, so we don't want to recycle the reader.
				peReader.Dispose();

				// If this was the last rental to be returned, we can close the file.
				if (_readersRentedOutCount is 0)
					_memoryMappedFile.Dispose();
			}
			else
			{
				// Store this in the cache for reuse later.
				_peReaders.Push((peReader, mdReader));
			}
		}
	}

	public void Dispose()
	{
		// Prepares to close the file handle and release resources as soon as all rentals have been returned.
		lock (_lock)
		{
			_disposed = true;

			// Drain our cache of readers (the ones that aren't currently being used).
			while (_peReaders.Count > 0)
				_peReaders.Pop().PEReader.Dispose();

			// Close the file if we have no readers rented out.
			if (_readersRentedOutCount is 0)
				_memoryMappedFile.Dispose();
		}
	}

	internal class WinMDReaderRental : IDisposable
	{
		private (PEReader PEReader, MetadataReader MDReader, WinMDFile File)? state;

		internal WinMDReaderRental(PEReader peReader, MetadataReader mdReader, WinMDFile file)
		{
			this.state = (peReader, mdReader, file);
		}

		internal MetadataReader Value => this.state?.MDReader ?? throw new ObjectDisposedException(typeof(WinMDReaderRental).FullName);

		public void Dispose()
		{
			if (this.state is (PEReader peReader, MetadataReader mdReader, WinMDFile file))
			{
				file.ReturnWinMDReader(peReader, mdReader);
				this.state = null;
			}
		}
	}
}
