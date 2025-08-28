// Copyright (c) 0x5BFA.

using System.IO.MemoryMappedFiles;
using System.Reflection.PortableExecutable;

namespace CsWin32Ex;

[DebuggerDisplay($"{{{nameof(Path)} ({nameof(_lastWriteTimeUtc)}),nq}}")]
internal class WinMDFile : IDisposable
{
	private static readonly Dictionary<string, WinMDFile> _cachedWinMDFiles = new(StringComparer.OrdinalIgnoreCase);

	private readonly object _lock = new();
	private readonly MemoryMappedFile _memoryMappedFile;
	private readonly Stack<(PEReader PEReader, MetadataReader MDReader)> _readers = new();
	private readonly Dictionary<Platform?, WinMDFileIndexer> _indexers = [];
	private readonly DateTime _lastWriteTimeUtc;

	private uint _readersRentedOutCount;
	private bool _disposed;

	internal string Path { get; }

	private WinMDFile(string fullPathToWinMD)
	{
		Path = fullPathToWinMD;
		_lastWriteTimeUtc = File.GetLastWriteTimeUtc(fullPathToWinMD);

		// When using FileShare.Delete, the OS will allow the file to be deleted, but it does not disrupt our ability to read the file while our handle is open.
		// The file may be recreated on disk as well, and we'll keep reading the original file until we close that handle.
		// We may also open the new file while holding the old handle, at which point we have handles open to both versions of the file concurrently.
		var winMDFileStream = new FileStream(fullPathToWinMD, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
		_memoryMappedFile = MemoryMappedFile.CreateFromFile(winMDFileStream, mapName: null, capacity: 0, MemoryMappedFileAccess.Read, HandleInheritability.None, leaveOpen: false);
	}

	internal static WinMDFile Create(string fullPathToWinMD)
	{
		lock (_cachedWinMDFiles)
		{
			if (_cachedWinMDFiles.TryGetValue(fullPathToWinMD, out WinMDFile? winMDFile))
			{
				// Reuse the instance since the WinMD file is cached & not updated externally
				if (winMDFile._lastWriteTimeUtc == File.GetLastWriteTimeUtc(fullPathToWinMD))
					return winMDFile;

				// Remove from the cache since the WinMD file got updated externally.
				_cachedWinMDFiles.Remove(fullPathToWinMD);
				winMDFile.Dispose();
			}

			// Create a new instance and cache it.
			_cachedWinMDFiles.Add(fullPathToWinMD, winMDFile = new(fullPathToWinMD));
			return winMDFile;
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
			if (_readers.Count > 0)
			{
				(peReader, metadataReader) = _readers.Pop();
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

	internal WinMDFileIndexer GetWinMDIndex(Platform? platform)
	{
		lock (_lock)
		{
			if (!_indexers.TryGetValue(platform, out WinMDFileIndexer? index))
				_indexers.Add(platform, index = new WinMDFileIndexer(this, platform));

			return index;
		}
	}

	internal void ReturnWinMDReader(PEReader peReader, MetadataReader mdReader)
	{
		lock (_lock)
		{
			_readersRentedOutCount--;

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
				_readers.Push((peReader, mdReader));
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
			while (_readers.Count > 0)
				_readers.Pop().PEReader.Dispose();

			// Close the file if we have no readers rented out.
			if (_readersRentedOutCount is 0)
				_memoryMappedFile.Dispose();
		}
	}
}
