using Microsoft.Web.WebView2.Core;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;

namespace WInUI3_POC
{
    /*
     * ???????????????????????????????????????????????????????????????????????????
     *  2 GB LIMIT — SCOPE
     * ???????????????????????????????????????????????????????????????????????????
     *
     *  CoreWebView2SharedBuffer's 2 GB ceiling is PER-PROCESS, not per-system.
     *
     *  Every CoreWebView2SharedBuffer created from any CoreWebView2Environment
     *  inside the same OS process counts against that single process-wide 2 GB
     *  address-space budget.  A second process running the same application gets
     *  its own independent 2 GB budget.
     *
     *  MaxTotalBytes below is therefore set to 2 GB and is enforced across all
     *  slabs allocated by this manager in the current process.
     *
     * ???????????????????????????????????????????????????????????????????????????
     *  POOL LAYOUT  (grows on demand, one slab at a time)
     * ???????????????????????????????????????????????????????????????????????????
     *
     *  The pool is a list of "slabs".  Each slab is one CoreWebView2SharedBuffer
     *  of SlabSize bytes (default 5 MB) divided into SegmentsPerSlab fixed
     *  1 MB segments.  New slabs are created on demand when all existing
     *  segments are occupied, up to the 2 GB process-wide ceiling.
     *
     *  Slab 0 (5 MB)          Slab 1 (5 MB)             ...
     *  ????????????????       ????????????????
     *  ?S0?S1?S2?S3?S4?       ?S0?S1?S2?S3?S4?          ...
     *  ????????????????       ????????????????
     *
     *  A window's allocation is identified by a SegmentHandle { SlabIndex,
     *  SegmentIndex }.  MainWindow stores a SegmentHandle? instead of a plain
     *  int so it always knows which slab's buffer to use.
     *
     *  Only the slab that owns the window's segment is posted to that window's
     *  WebView2 — not all slabs.
     *
     * ???????????????????????????????????????????????????????????????????????????
     *  SEGMENT INTERNAL LAYOUT  (identical for every segment in every slab)
     * ???????????????????????????????????????????????????????????????????????????
     *
     *  Byte offset            Size      Field
     *  within segment
     *  ?????????????????????  ????????  ??????????????????????????????????????
     *  +0                     4 bytes   OWNER_LEN  – UTF-8 byte-length of the
     *                                   window-id string stored in OWNER_ID.
     *                                   0 means "segment is free / unowned".
     *
     *  +4                    36 bytes   OWNER_ID   – UTF-8 window-id GUID,
     *                                   zero-padded to fill the field.
     *
     *  +40               1 048 536 B    CONTENT    – JS writes here;
     *                                   C# reads back after save.
     *
     *  ???????????????????????????????????????????????????????????????????????
     *  ?  HEADER  ?                      CONTENT                             ?
     *  ?  40 B    ?              1 048 536 bytes                              ?
     *  ???????????????????????????????????????????????????????????????????????
     *   ?          ?
     *   ?          ??? ContentOffset = SegmentBase(seg) + HeaderSize
     *   ????????????? SegmentBase(seg) = seg × 1 048 576  (within the slab)
     * ???????????????????????????????????????????????????????????????????????????
     */

    // ?? public handle returned to callers ????????????????????????????????????

    /// <summary>
    /// Identifies a specific 1 MB segment inside a specific slab.
    /// Stored by MainWindow instead of a bare int.
    /// </summary>
    public readonly record struct SegmentHandle(int SlabIndex, int SegmentIndex)
    {
        public static readonly SegmentHandle Invalid = new(-1, -1);
        public bool IsValid => SlabIndex >= 0 && SegmentIndex >= 0;
    }

    // ?? manager ??????????????????????????????????????????????????????????????

    /// <summary>
    /// Manages a dynamically growing pool of <see cref="CoreWebView2SharedBuffer"/>
    /// slabs.  Each slab is 5 MB divided into five 1 MB segments.  New slabs are
    /// added on demand until the 2 GB per-process ceiling is reached.
    /// </summary>
    public sealed class SharedBufferManager : IDisposable
    {
        // ?? geometry constants ???????????????????????????????????????????????

        /// <summary>Each slab is 5 MB.</summary>
        public const int SlabSizeMB = 5;

        /// <summary>Each segment inside a slab is 1 MB.</summary>
        public const int SegmentSizeMB = 1;

        /// <summary>Segments per slab (5 MB / 1 MB = 5).</summary>
        public const int SegmentsPerSlab = SlabSizeMB / SegmentSizeMB;

        public static readonly ulong SlabSize    = (ulong)(SlabSizeMB    * 1024 * 1024);
        public static readonly ulong SegmentSize = (ulong)(SegmentSizeMB * 1024 * 1024);

        // 2 GB per-process limit imposed by WebView2.
        // This is a process-wide budget shared across all slabs.
        public static readonly ulong MaxTotalBytes = 2UL * 1024 * 1024 * 1024;

        // ?? header field layout (offsets relative to the segment start) ??????

        private const int HeaderField_OwnerLen = 0;   // bytes [0..3]
        private const int HeaderField_OwnerId  = 4;   // bytes [4..39]

        /// <summary>Maximum UTF-8 bytes for the window-id (32-char GUID + 4 spare).</summary>
        public const int MaxWindowIdBytes = 36;

        /// <summary>Header size = OWNER_LEN(4) + OWNER_ID(36) = 40 bytes.</summary>
        public const int HeaderSize = HeaderField_OwnerId + MaxWindowIdBytes;  // 40

        /// <summary>Usable content bytes per segment.</summary>
        public static ulong ContentSize => SegmentSize - (ulong)HeaderSize;

        // ?? private state ????????????????????????????????????????????????????

        private readonly CoreWebView2Environment _env;
        private readonly object _lock = new();

        // One entry per slab created so far.
        private readonly List<CoreWebView2SharedBuffer> _slabs = new();

        // Free segments across all slabs: each entry is (slabIndex, segmentIndex).
        private readonly Queue<SegmentHandle> _freeSegments = new();

        // windowId ? handle  (for lookup and release)
        private readonly Dictionary<string, SegmentHandle> _windowToHandle = new();

        // handle ? windowId  (for cross-checking)
        private readonly Dictionary<SegmentHandle, string> _handleToWindow = new();

        // ?? construction / disposal ??????????????????????????????????????????

        public SharedBufferManager(CoreWebView2Environment env)
        {
            _env = env;
            GrowPool();  // allocate the first slab immediately
        }

        public void Dispose()
        {
            lock (_lock)
            {
                foreach (var slab in _slabs)
                    (slab as IDisposable)?.Dispose();
                _slabs.Clear();
            }
        }

        // ?? public API ???????????????????????????????????????????????????????

        /// <summary>
        /// Allocates the next free segment for <paramref name="windowId"/>.
        /// Grows the pool by one slab if needed and the 2 GB ceiling allows it.
        /// Returns <see cref="SegmentHandle.Invalid"/> when no capacity is left.
        /// </summary>
        public SegmentHandle AllocateSegment(string windowId)
        {
            lock (_lock)
            {
                // Grow if exhausted (and within 2 GB budget).
                if (_freeSegments.Count == 0)
                    GrowPool();

                if (_freeSegments.Count == 0)
                {
                    Debug.WriteLine($"[SharedBufferManager] Pool exhausted (2 GB limit). Cannot allocate for '{windowId}'.");
                    return SegmentHandle.Invalid;
                }

                SegmentHandle handle = _freeSegments.Dequeue();
                _windowToHandle[windowId] = handle;
                _handleToWindow[handle]   = windowId;

                WriteHeader(handle, windowId);

                Debug.WriteLine(
                    $"[SharedBufferManager] Allocated slab={handle.SlabIndex} seg={handle.SegmentIndex} " +
                    $"? window '{windowId}'. Free segments remaining: {_freeSegments.Count}");
                return handle;
            }
        }

        /// <summary>
        /// Returns the <see cref="CoreWebView2SharedBuffer"/> for the slab
        /// referenced by <paramref name="handle"/>.
        /// Post this (and only this) to the window that owns the handle.
        /// </summary>
        public CoreWebView2SharedBuffer GetSlabBuffer(SegmentHandle handle)
        {
            lock (_lock)
                return _slabs[handle.SlabIndex];
        }

        /// <summary>
        /// Absolute byte offset of the CONTENT area for <paramref name="handle"/>
        /// within its slab buffer.  Send this to JS as <c>segmentOffset</c>.
        /// </summary>
        public static ulong ContentOffset(SegmentHandle handle) =>
            SegmentBase(handle.SegmentIndex) + (ulong)HeaderSize;

        /// <summary>
        /// Cross-check: reads the OWNER_ID field from the segment header and
        /// verifies it matches <paramref name="windowId"/>.
        /// </summary>
        public bool VerifyWindowId(SegmentHandle handle, string windowId)
        {
            string stamped = ReadOwnerId(handle);
            bool ok = stamped == windowId;
            if (!ok)
                Debug.WriteLine(
                    $"[SharedBufferManager] Cross-check FAILED slab={handle.SlabIndex} " +
                    $"seg={handle.SegmentIndex}: header='{stamped}', expected='{windowId}'.");
            return ok;
        }

        /// <summary>
        /// Reads <paramref name="byteCount"/> bytes from the CONTENT area of
        /// <paramref name="handle"/> into a new managed byte array.
        /// </summary>
        public byte[] ReadSegmentContent(SegmentHandle handle, int byteCount)
        {
            if ((ulong)byteCount > ContentSize)
                throw new ArgumentOutOfRangeException(nameof(byteCount),
                    $"byteCount {byteCount} exceeds content area {ContentSize} B.");

            long contentStart = (long)ContentOffset(handle);

            using Stream stream = _slabs[handle.SlabIndex].OpenStream().AsStreamForRead();
            stream.Seek(contentStart, SeekOrigin.Begin);

            byte[] buffer = new byte[byteCount];
            stream.ReadExactly(buffer);
            return buffer;
        }

        /// <summary>
        /// Releases the segment for <paramref name="windowId"/>: clears the header,
        /// removes dictionary entries, returns the segment to the free pool.
        /// </summary>
        public void ReleaseSegment(string windowId)
        {
            lock (_lock)
            {
                if (!_windowToHandle.TryGetValue(windowId, out SegmentHandle handle))
                {
                    Debug.WriteLine($"[SharedBufferManager] ReleaseSegment: window '{windowId}' has no allocation.");
                    return;
                }

                ClearHeader(handle);
                _windowToHandle.Remove(windowId);
                _handleToWindow.Remove(handle);
                _freeSegments.Enqueue(handle);

                Debug.WriteLine(
                    $"[SharedBufferManager] Released slab={handle.SlabIndex} seg={handle.SegmentIndex} " +
                    $"from window '{windowId}'. Free segments: {_freeSegments.Count}");
            }
        }

        // ?? private pool growth ??????????????????????????????????????????????

        /// <summary>
        /// Adds one new slab to the pool if the 2 GB process-wide ceiling allows.
        /// Must be called inside <c>_lock</c>.
        /// </summary>
        private void GrowPool()
        {
            ulong currentTotal = (ulong)_slabs.Count * SlabSize;
            if (currentTotal + SlabSize > MaxTotalBytes)
            {
                Debug.WriteLine(
                    $"[SharedBufferManager] Cannot grow: would exceed 2 GB process limit " +
                    $"(current={currentTotal / (1024 * 1024)} MB).");
                return;
            }

            int slabIndex = _slabs.Count;
            CoreWebView2SharedBuffer slab = _env.CreateSharedBuffer(SlabSize);
            _slabs.Add(slab);

            for (int seg = 0; seg < SegmentsPerSlab; seg++)
                _freeSegments.Enqueue(new SegmentHandle(slabIndex, seg));

            Debug.WriteLine(
                $"[SharedBufferManager] Slab {slabIndex} created ({SlabSizeMB} MB). " +
                $"Total allocated: {(ulong)(_slabs.Count) * SlabSize / (1024 * 1024)} MB / 2048 MB. " +
                $"Free segments: {_freeSegments.Count}");
        }

        // ?? segment geometry helpers ?????????????????????????????????????????

        // Byte offset of segment segIdx within its slab (NOT the global buffer).
        private static ulong SegmentBase(int segIdx) => (ulong)segIdx * SegmentSize;

        // ?? private header helpers ???????????????????????????????????????????

        private void WriteHeader(SegmentHandle handle, string windowId)
        {
            byte[] idBytes = Encoding.UTF8.GetBytes(windowId);
            if (idBytes.Length > MaxWindowIdBytes)
                throw new ArgumentException($"Window id too long ({idBytes.Length} B, max {MaxWindowIdBytes} B).");

            byte[] header = new byte[HeaderSize];
            BitConverter.GetBytes(idBytes.Length).CopyTo(header, HeaderField_OwnerLen);
            idBytes.CopyTo(header, HeaderField_OwnerId);

            long headerStart = (long)SegmentBase(handle.SegmentIndex);
            using Stream stream = _slabs[handle.SlabIndex].OpenStream().AsStreamForWrite();
            stream.Seek(headerStart, SeekOrigin.Begin);
            stream.Write(header, 0, header.Length);
        }

        private void ClearHeader(SegmentHandle handle)
        {
            byte[] zeros = new byte[HeaderSize];
            long headerStart = (long)SegmentBase(handle.SegmentIndex);

            using Stream stream = _slabs[handle.SlabIndex].OpenStream().AsStreamForWrite();
            stream.Seek(headerStart, SeekOrigin.Begin);
            stream.Write(zeros, 0, zeros.Length);
        }

        private string ReadOwnerId(SegmentHandle handle)
        {
            byte[] header = new byte[HeaderSize];
            long headerStart = (long)SegmentBase(handle.SegmentIndex);

            using Stream stream = _slabs[handle.SlabIndex].OpenStream().AsStreamForRead();
            stream.Seek(headerStart, SeekOrigin.Begin);
            stream.ReadExactly(header);

            int len = BitConverter.ToInt32(header, HeaderField_OwnerLen);
            if (len <= 0 || len > MaxWindowIdBytes)
                return string.Empty;

            return Encoding.UTF8.GetString(header, HeaderField_OwnerId, len);
        }
    }
}

