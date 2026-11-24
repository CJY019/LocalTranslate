using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;

namespace LocalTranslate {
    internal static class ClipboardSelection {
        const uint UnicodeText = 13;
        const uint Bitmap = 2;
        const uint MetafilePicture = 3;
        const uint Tiff = 6;
        const uint Palette = 9;
        const uint EnhancedMetafile = 14;
        const uint FileDrop = 15;
        const long MaximumBackupBytes = 64L * 1024 * 1024;
        const int MaximumTextBytes = 2 * 1024 * 1024;
        const int BackupTimeoutMilliseconds = 1500;
        const int CopyTimeoutMilliseconds = 750;
        const int Control = 0x11;
        const int C = 0x43;
        static int reading, backingUp;

        // Called on the dispatcher after an explicit click in an approved application.
        internal static Task<string> TryReadAsync(IntPtr sourceWindow, IntPtr clipboardWindow, Func<bool> isCurrent, Action onBackupUnavailable = null) {
            return TryReadCoreAsync(sourceWindow, clipboardWindow, isCurrent, false, onBackupUnavailable);
        }

        // The caller obtains separate consent to leave the source application's copy on the clipboard.
        internal static Task<string> TryReadReplacingAsync(IntPtr sourceWindow, IntPtr clipboardWindow, Func<bool> isCurrent) {
            return TryReadCoreAsync(sourceWindow, clipboardWindow, isCurrent, true, null);
        }

        static async Task<string> TryReadCoreAsync(IntPtr sourceWindow, IntPtr clipboardWindow, Func<bool> isCurrent,
            bool replaceClipboard, Action onBackupUnavailable) {
            SelectionDiagnostics.Record(replaceClipboard ? "copy-replace-start" : "copy-start");
            if (sourceWindow == IntPtr.Zero || clipboardWindow == IntPtr.Zero || !isCurrent()) {
                SelectionDiagnostics.Record("copy-request-invalid");
                return null;
            }
            uint sourceProcess;
            uint sourceThread = GetWindowThreadProcessId(sourceWindow, out sourceProcess);
            if (sourceThread == 0 || sourceProcess == GetCurrentProcessId() || WindowProcess(clipboardWindow) != GetCurrentProcessId()) {
                SelectionDiagnostics.Record("copy-window-invalid");
                return null;
            }
            if (!IsForeground(sourceWindow, sourceProcess)) {
                SelectionDiagnostics.Record("copy-foreground-lost");
                return null;
            }
            if (Interlocked.CompareExchange(ref reading, 1, 0) != 0) {
                SelectionDiagnostics.Record("copy-already-reading");
                return null;
            }

            try {
                var backup = replaceClipboard ? default(ClipboardBackup) : await BackUpClipboardAsync(clipboardWindow);
                using (var snapshot = backup.Snapshot) {
                    if (snapshot == null && !replaceClipboard) {
                        if (backup.Unavailable && isCurrent() && IsForeground(sourceWindow, sourceProcess)) onBackupUnavailable?.Invoke();
                        return null;
                    }
                    if (!isCurrent() || !IsForeground(sourceWindow, sourceProcess)) {
                        SelectionDiagnostics.Record("copy-cancelled-after-backup");
                        return null;
                    }
                    if (await Task.Run(() => HasProtectedFocus(sourceThread, sourceProcess))) return null;

                    ClipboardStamp initial;
                    IntPtr previousText = IntPtr.Zero;
                    if (replaceClipboard) {
                        var pending = Task.Run(() => CaptureCopyBaseline(clipboardWindow, sourceProcess));
                        if (await Task.WhenAny(pending, Task.Delay(BackupTimeoutMilliseconds)) != pending) {
                            SelectionDiagnostics.Record("copy-replace-baseline-timeout");
                            return null;
                        }
                        var baseline = await pending;
                        if (!baseline.HasValue) {
                            SelectionDiagnostics.Record("copy-replace-baseline-unverified");
                            return null;
                        }
                        initial = baseline.Value.Stamp;
                        previousText = baseline.Value.PreviousText;
                    } else initial = snapshot.Stamp;
                    if (!isCurrent() || !IsForeground(sourceWindow, sourceProcess)) {
                        SelectionDiagnostics.Record("copy-cancelled-after-focus-check");
                        return null;
                    }
                    if (CopyKeysAreDown()) {
                        SelectionDiagnostics.Record("copy-modifier-or-c-key-down");
                        return null;
                    }
                    if (!ClipboardStamp.Read().Equals(initial)) {
                        SelectionDiagnostics.Record("copy-clipboard-changed-before-input");
                        return null;
                    }
                    if (!SendCopy()) {
                        SelectionDiagnostics.Record("copy-input-failed");
                        return null;
                    }
                    var elapsed = Stopwatch.StartNew();
                    SelectionDiagnostics.Record("copy-input-sent");
                    ClipboardStamp? observedCopy = null;
                    bool selectionActive = true;
                    while (elapsed.ElapsedMilliseconds < CopyTimeoutMilliseconds) {
                        await Task.Delay(20);
                        selectionActive = selectionActive && isCurrent() && IsForeground(sourceWindow, sourceProcess);
                        if (GetOpenClipboardWindow() != IntPtr.Zero) continue;

                        var copy = ClipboardStamp.Read();
                        if (observedCopy.HasValue && !observedCopy.Value.Equals(copy)) {
                            SelectionDiagnostics.Record("copy-clipboard-superseded");
                            return null;
                        }
                        // The same owner can replace its data with delayed text before the sequence changes.
                        bool initialStamp = copy.Equals(initial);
                        if (initialStamp && (!replaceClipboard || WindowProcess(initial.Owner) != sourceProcess || !selectionActive)) continue;
                        if (copy.Owner == IntPtr.Zero || WindowProcess(copy.Owner) != sourceProcess) {
                            SelectionDiagnostics.Record(copy.Owner == IntPtr.Zero ? "copy-owner-missing" : "copy-owner-mismatch");
                            return null;
                        }
                        if (!initialStamp) observedCopy = copy;

                        string text = null;
                        Func<bool> canRead = () => selectionActive && elapsed.ElapsedMilliseconds < CopyTimeoutMilliseconds && isCurrent() && IsForeground(sourceWindow, sourceProcess);
                        if (await Task.Run(() => replaceClipboard
                            ? TryReadReplacing(clipboardWindow, initial, copy, previousText, sourceProcess, canRead, out text)
                            : snapshot.TryReadAndRestore(clipboardWindow, copy, sourceProcess, canRead, out text))) {
                            if (selectionActive && isCurrent() && IsForeground(sourceWindow, sourceProcess)) return text;
                            SelectionDiagnostics.Record("copy-cancelled-after-input");
                            return null;
                        }
                    }
                    SelectionDiagnostics.Record(observedCopy.HasValue ? "copy-clipboard-locked-until-timeout" : "copy-output-timeout");
                }
                return null;
            } finally {
                Interlocked.Exchange(ref reading, 0);
            }
        }

        struct ClipboardBackup {
            internal readonly ClipboardSnapshot Snapshot;
            internal readonly bool Unavailable;

            internal ClipboardBackup(ClipboardSnapshot snapshot, bool unavailable) { Snapshot = snapshot; Unavailable = unavailable; }
        }

        struct CopyBaseline {
            internal readonly ClipboardStamp Stamp;
            internal readonly IntPtr PreviousText;

            internal CopyBaseline(ClipboardStamp stamp, IntPtr previousText) { Stamp = stamp; PreviousText = previousText; }
        }

        static CopyBaseline? CaptureCopyBaseline(IntPtr clipboardWindow, uint sourceProcess) {
            if (!OpenClipboard(clipboardWindow)) return null;
            try {
                var initial = ClipboardStamp.Read();
                if (WindowProcess(initial.Owner) != sourceProcess || !IsClipboardFormatAvailable(UnicodeText))
                    return new CopyBaseline(initial, IntPtr.Zero);

                // Materialize delayed text before input. Retain only the handle's identity to recognize stale data.
                var previousText = GetClipboardData(UnicodeText);
                var rendered = ClipboardStamp.Read();
                if (previousText == IntPtr.Zero || rendered.Owner != initial.Owner) return null;
                return new CopyBaseline(rendered, previousText);
            } finally { CloseClipboard(); }
        }

        // False lets the caller retry when the clipboard is busy or same-owner output has not arrived.
        static bool TryReadReplacing(IntPtr clipboardWindow, ClipboardStamp initial, ClipboardStamp copy, IntPtr previousText,
            uint sourceProcess, Func<bool> canRead, out string text) {
            text = null;
            if (!OpenClipboard(clipboardWindow)) return false;
            string outcome = null;
            try {
                if (!ClipboardStamp.Read().Equals(copy) || WindowProcess(copy.Owner) != sourceProcess) {
                    outcome = "copy-clipboard-changed-before-read";
                    return true;
                }
                if (!canRead()) return true;

                var handle = GetClipboardData(UnicodeText);
                var rendered = ClipboardStamp.Read();
                if (rendered.Owner != copy.Owner || WindowProcess(rendered.Owner) != sourceProcess) {
                    outcome = "copy-output-freshness-unconfirmed";
                    return true;
                }
                if (rendered.Sequence == initial.Sequence) {
                    if (copy.Equals(initial)) return false;
                    outcome = "copy-output-freshness-unconfirmed";
                    return true;
                }
                if (previousText != IntPtr.Zero && handle == previousText && rendered.Owner == initial.Owner) {
                    outcome = "copy-replace-previous-text";
                    return true;
                }
                if (canRead()) text = ReadUnicodeText(handle);
                outcome = text == null ? "copy-unicode-unavailable" : "copy-replace-captured";
                return true;
            } finally {
                if (CloseClipboard() && outcome != null) SelectionDiagnostics.Record(outcome);
            }
        }

        static async Task<ClipboardBackup> BackUpClipboardAsync(IntPtr clipboardWindow) {
            if (Interlocked.CompareExchange(ref backingUp, 1, 0) != 0) {
                SelectionDiagnostics.Record("copy-backup-already-running");
                return default(ClipboardBackup);
            }
            var completed = new TaskCompletionSource<ClipboardBackup>(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() => {
                ClipboardSnapshot snapshot = null;
                bool unavailable = false;
                Exception failure = null;
                try { snapshot = ClipboardSnapshot.TryCreate(clipboardWindow, out unavailable); }
                catch (Exception error) { failure = error; }
                finally { Interlocked.Exchange(ref backingUp, 0); }
                // A completed request can immediately be followed by another click.
                if (failure == null) completed.SetResult(new ClipboardBackup(snapshot, unavailable));
                else {
                    SelectionDiagnostics.Record("copy-backup-exception");
                    completed.SetException(failure);
                }
            }) { IsBackground = true };
            try {
                thread.SetApartmentState(ApartmentState.STA);
                thread.Start();
            } catch {
                Interlocked.Exchange(ref backingUp, 0);
                throw;
            }
            if (await Task.WhenAny(completed.Task, Task.Delay(BackupTimeoutMilliseconds)) == completed.Task) return await completed.Task;
            SelectionDiagnostics.Record("copy-backup-timeout");
            _ = completed.Task.ContinueWith(result => {
                if (result.Status == TaskStatus.RanToCompletion) result.Result.Snapshot?.Dispose();
                else if (result.IsFaulted) Trace.TraceWarning("Clipboard backup failed: {0}", result.Exception.GetBaseException().Message);
            }, TaskScheduler.Default);
            return default(ClipboardBackup);
        }

        static bool IsForeground(IntPtr window, uint process) {
            return GetForegroundWindow() == window && WindowProcess(window) == process;
        }

        static uint WindowProcess(IntPtr window) {
            if (window == IntPtr.Zero) return 0;
            uint process;
            return GetWindowThreadProcessId(window, out process) == 0 ? 0 : process;
        }

        static bool HasProtectedFocus(uint thread, uint process) {
            var info = new GuiThreadInfo { Size = (uint)Marshal.SizeOf(typeof(GuiThreadInfo)) };
            if (!GetGUIThreadInfo(thread, ref info) || WindowProcess(info.Focus) != process) return UnsafeFocus("copy-native-focus-unverified");
            var name = new StringBuilder(256);
            if (GetClassName(info.Focus, name, name.Capacity) == 0) return UnsafeFocus("copy-native-focus-class-unverified");
            string windowClass = name.ToString();
            bool edit = windowClass.Equals("Edit", StringComparison.OrdinalIgnoreCase) || windowClass.StartsWith("RichEdit", StringComparison.OrdinalIgnoreCase);
            if (edit && (GetWindowLong(info.Focus, -16) & 0x20) != 0) return UnsafeFocus("copy-native-password-focus"); // ES_PASSWORD

            try {
                var focused = AutomationElement.FocusedElement;
                if (focused == null || focused.Current.ProcessId != process) return UnsafeFocus("copy-uia-focus-unverified");
                var element = focused;
                for (int depth = 0; element != null && element.Current.ProcessId == process; depth++) {
                    if (depth >= 32) return UnsafeFocus("copy-uia-focus-depth-exceeded");
                    if (element.Current.IsPassword) return UnsafeFocus("copy-uia-password-focus");
                    element = TreeWalker.RawViewWalker.GetParent(element);
                }
                return false;
            } catch (ElementNotAvailableException) { return UnsafeFocus("copy-uia-focus-unavailable"); }
            catch (COMException) { return UnsafeFocus("copy-uia-focus-provider-error"); }
            catch (InvalidOperationException) { return UnsafeFocus("copy-uia-focus-provider-error"); }
            catch (UnauthorizedAccessException) { return UnsafeFocus("copy-uia-focus-provider-error"); }
        }

        static bool UnsafeFocus(string reason) {
            SelectionDiagnostics.Record(reason);
            return true;
        }

        static bool CopyKeysAreDown() {
            return GetAsyncKeyState(0x10) < 0 || GetAsyncKeyState(Control) < 0 || GetAsyncKeyState(0x12) < 0 ||
                GetAsyncKeyState(0x5b) < 0 || GetAsyncKeyState(0x5c) < 0 || GetAsyncKeyState(C) < 0;
        }

        static bool SendCopy() {
            var keys = new[] { Input.Key(Control, false), Input.Key(C, false), Input.Key(C, true), Input.Key(Control, true) };
            int size = Marshal.SizeOf(typeof(Input));
            uint sent = SendInput((uint)keys.Length, keys, size);
            if (sent == 2) SendInput(1, new[] { Input.Key(C, true) }, size);
            if (sent > 0 && sent < keys.Length) SendInput(1, new[] { Input.Key(Control, true) }, size);
            return sent >= 2;
        }

        static string ReadUnicodeText(IntPtr handle) {
            if (handle == IntPtr.Zero) return null;
            ulong bytes = GlobalSize(handle).ToUInt64();
            if (bytes < 2 || bytes > MaximumTextBytes) return null;
            IntPtr value = GlobalLock(handle);
            if (value == IntPtr.Zero) return null;
            try {
                var characters = new char[(int)bytes / 2];
                Marshal.Copy(value, characters, 0, characters.Length);
                int terminator = Array.IndexOf(characters, '\0');
                if (terminator < 0) return null;
                string text = new string(characters, 0, terminator).Trim();
                return text.Length == 0 ? null : text;
            } finally {
                GlobalUnlock(handle);
            }
        }

        internal struct ClipboardStamp : IEquatable<ClipboardStamp> {
            internal readonly uint Sequence;
            internal readonly IntPtr Owner;

            ClipboardStamp(uint sequence, IntPtr owner) { Sequence = sequence; Owner = owner; }

            internal static ClipboardStamp Read() {
                return new ClipboardStamp(GetClipboardSequenceNumber(), GetClipboardOwner());
            }

            public bool Equals(ClipboardStamp other) { return Sequence == other.Sequence && Owner == other.Owner; }
        }

        internal sealed class ClipboardSnapshot : IDisposable {
            const string RustDeskOwnerFormat = "dyn.com.rustdesk.owner";
            const string FormatEnumerationFailed = "copy-backup-format-enumeration-failed";
            const string FormatsUnsupported = "copy-backup-formats-unsupported";
            const string OleContentUnverified = "copy-backup-ole-content-unverified";
            const string FormatUnavailable = "copy-backup-format-unavailable";
            const string FormatSizeInvalid = "copy-backup-format-size-invalid";
            const string DuplicateFailed = "copy-backup-duplicate-failed";
            static readonly HashSet<uint> GlobalFormats = new HashSet<uint> { 1, Tiff, 7, 8, UnicodeText, FileDrop, 16, 17 }; // Text, images, file list, and locale.
            static readonly HashSet<string> RegisteredGlobalFormats = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
                "HTML Format", "Rich Text Format", "Rich Text Format Without Objects",
                "PNG", "JFIF", "UniformResourceLocator", "UniformResourceLocatorW",
                "CanIncludeInClipboardHistory", "CanUploadToCloudClipboard", "ExcludeClipboardContentFromMonitorProcessing"
            };

            sealed class Entry {
                internal readonly uint Format;
                internal IntPtr Handle;
                internal Entry(uint format, IntPtr handle) { Format = format; Handle = handle; }
            }

            readonly List<Entry> entries = new List<Entry>();
            internal ClipboardStamp Stamp { get; private set; }

            static TYMED FormatMedium(uint format) {
                if (format == Bitmap || format == Palette) return TYMED.TYMED_GDI;
                if (format == MetafilePicture) return TYMED.TYMED_MFPICT;
                if (format == EnhancedMetafile) return TYMED.TYMED_ENHMF;
                return TYMED.TYMED_HGLOBAL;
            }

            static List<uint> EnumerateFormats() {
                var formats = new List<uint>();
                uint format = 0;
                while (true) {
                    SetLastError(0);
                    format = EnumClipboardFormats(format);
                    if (format == 0) return Marshal.GetLastWin32Error() == 0 ? formats : null;
                    formats.Add(format);
                }
            }

            static bool CheckFormats(List<uint> formats, out uint dataObject, out uint olePrivateData, out uint rustDeskOwner) {
                dataObject = olePrivateData = rustDeskOwner = 0;
                var name = new StringBuilder(256);
                foreach (uint format in formats) {
                    if (GlobalFormats.Contains(format) || FormatMedium(format) != TYMED.TYMED_HGLOBAL) continue;
                    if (format < 0xc000 || GetClipboardFormatName(format, name, name.Capacity) == 0) return false;
                    string registered = name.ToString();
                    if (String.Equals(registered, "DataObject", StringComparison.OrdinalIgnoreCase)) dataObject = format;
                    else if (String.Equals(registered, "Ole Private Data", StringComparison.OrdinalIgnoreCase)) olePrivateData = format;
                    else if (String.Equals(registered, RustDeskOwnerFormat, StringComparison.OrdinalIgnoreCase)) rustDeskOwner = format;
                    else if (!RegisteredGlobalFormats.Contains(registered)) return false;
                }

                if (dataObject == 0 && olePrivateData == 0) return true;
                // A Shell data object can also manage paste and move notifications.
                return dataObject != 0 && olePrivateData != 0 && rustDeskOwner == 0 && !formats.Contains(FileDrop);
            }

            static bool HasRestorableOleContent(ClipboardStamp initial, List<uint> nativeFormats, uint dataObject, uint olePrivateData) {
                if (!ClipboardStamp.Read().Equals(initial) || Thread.CurrentThread.GetApartmentState() != ApartmentState.STA) return false;
                if (OleInitialize(IntPtr.Zero) < 0) return false;
                IDataObject data = null;
                IEnumFORMATETC formats = null;
                try {
                    if (OleGetClipboard(out data) < 0 || data == null || !ClipboardStamp.Read().Equals(initial)) return false;
                    formats = data.EnumFormatEtc(DATADIR.DATADIR_GET);
                    if (formats == null) return false;
                    var offeredFormats = new HashSet<uint>();
                    var entry = new FORMATETC[1];
                    var fetched = new int[1];
                    for (int index = 0; index <= nativeFormats.Count; index++) {
                        int result = formats.Next(1, entry, fetched);
                        if (result == 1) {
                            if (offeredFormats.Count == 0 || fetched[0] != 0 || !ClipboardStamp.Read().Equals(initial)) return false;
                            // Win32 can synthesize standard formats. Registered content must also be advertised by OLE.
                            foreach (uint nativeFormat in nativeFormats) {
                                if (nativeFormat >= 0xc000 && nativeFormat != dataObject && nativeFormat != olePrivateData &&
                                    !offeredFormats.Contains(nativeFormat)) return false;
                            }
                            return true;
                        }
                        if (result != 0 || fetched[0] != 1) return false;
                        var offered = entry[0];
                        if (offered.ptd != IntPtr.Zero) {
                            Marshal.FreeCoTaskMem(offered.ptd);
                            return false;
                        }
                        uint format = unchecked((ushort)offered.cfFormat);
                        if (!nativeFormats.Contains(format) || format == dataObject || format == olePrivateData || !offeredFormats.Add(format) ||
                            offered.tymed != FormatMedium(format) || offered.dwAspect != DVASPECT.DVASPECT_CONTENT || offered.lindex != -1) return false;
                    }
                    return false;
                } catch (COMException) { return false; }
                catch (InvalidCastException) { return false; }
                finally {
                    if (formats != null && Marshal.IsComObject(formats)) Marshal.ReleaseComObject(formats);
                    if (data != null && Marshal.IsComObject(data)) Marshal.ReleaseComObject(data);
                    OleUninitialize();
                }
            }

            internal static ClipboardSnapshot TryCreate(IntPtr clipboardWindow) {
                return TryCreate(clipboardWindow, out _);
            }

            internal static ClipboardSnapshot TryCreate(IntPtr clipboardWindow, out bool unavailable) {
                unavailable = false;
                if (!OpenClipboard(clipboardWindow)) {
                    SelectionDiagnostics.Record("copy-backup-clipboard-busy");
                    return null;
                }
                var snapshot = new ClipboardSnapshot();
                bool complete = false;
                bool locked = true;
                string failure = null;
                try {
                    var initial = ClipboardStamp.Read();
                    var formats = EnumerateFormats();
                    uint dataObject, olePrivateData, rustDeskOwner;
                    if (formats == null) return RejectBackup(FormatEnumerationFailed, out failure);
                    if (!CheckFormats(formats, out dataObject, out olePrivateData, out rustDeskOwner)) return RejectBackup(FormatsUnsupported, out failure);

                    if (dataObject != 0) {
                        if (!CloseClipboard()) return RejectBackup("copy-backup-clipboard-close-failed", out failure);
                        locked = false;
                        // OLE opens the clipboard to inspect the source data object.
                        if (!HasRestorableOleContent(initial, formats, dataObject, olePrivateData)) {
                            string reason = ClipboardStamp.Read().Equals(initial) ? OleContentUnverified : "copy-backup-clipboard-changed";
                            return RejectBackup(reason, out failure);
                        }
                        if (!OpenClipboard(clipboardWindow)) return RejectBackup("copy-backup-clipboard-busy", out failure);
                        locked = true;
                        var currentFormats = EnumerateFormats();
                        if (!ClipboardStamp.Read().Equals(initial) || currentFormats == null || !SameFormats(formats, currentFormats))
                            return RejectBackup("copy-backup-clipboard-changed", out failure);
                    }

                    long remaining = MaximumBackupBytes;
                    foreach (uint format in formats) {
                        if (format == dataObject || format == olePrivateData) continue;
                        IntPtr source = GetClipboardData(format);
                        if (source == IntPtr.Zero) return RejectBackup(FormatUnavailable, out failure);
                        ulong bytes = GetBackupBytes(format, source);
                        if (bytes == 0 || bytes > (ulong)remaining || (format == rustDeskOwner && bytes != 1))
                            return RejectBackup(FormatSizeInvalid, out failure);
                        remaining -= (long)bytes;

                        IntPtr duplicate = format == EnhancedMetafile
                            ? CopyEnhMetaFile(source, null)
                            : OleDuplicateData(source, (ushort)format, 0);
                        if (duplicate == IntPtr.Zero || duplicate == source) return RejectBackup(DuplicateFailed, out failure);
                        snapshot.entries.Add(new Entry(format, duplicate));
                        if (format == rustDeskOwner && GlobalSize(duplicate).ToUInt64() != 1) return RejectBackup(DuplicateFailed, out failure);
                    }

                    snapshot.Stamp = ClipboardStamp.Read();
                    var finalFormats = EnumerateFormats();
                    // Some owners publish additional formats when a delayed format is requested.
                    if (snapshot.Stamp.Owner != initial.Owner || finalFormats == null || !SameFormats(formats, finalFormats))
                        return RejectBackup("copy-backup-clipboard-changed", out failure);
                    complete = true;
                    return snapshot;
                } finally {
                    bool released = !locked || CloseClipboard();
                    if (!complete) snapshot.Dispose();
                    if (released && failure != null) {
                        unavailable = failure == FormatsUnsupported || failure == OleContentUnverified || failure == FormatUnavailable ||
                            failure == FormatSizeInvalid || failure == DuplicateFailed;
                        SelectionDiagnostics.Record(failure);
                    }
                }
            }

            static ClipboardSnapshot RejectBackup(string reason, out string failure) {
                failure = reason;
                return null;
            }

            static ulong GetBackupBytes(uint format, IntPtr handle) {
                if (format == Bitmap) {
                    BitmapObject bitmap;
                    int size = Marshal.SizeOf(typeof(BitmapObject));
                    if (GetBitmapObject(handle, size, out bitmap) != size || bitmap.Type != 0 || bitmap.Width <= 0 || bitmap.Height <= 0 ||
                        bitmap.Stride <= 0 || bitmap.Stride % 2 != 0 || bitmap.Planes != 1 || bitmap.BitsPerPixel == 0 || bitmap.BitsPerPixel > 32) return 0;
                    ulong width = (ulong)bitmap.Width;
                    if ((ulong)bitmap.Stride < (width * bitmap.BitsPerPixel + 7) / 8) return 0;
                    // Budget for a 32-bit copy even when the original uses a smaller pixel format.
                    ulong stride = Math.Max((ulong)bitmap.Stride, width * 4);
                    const ulong bitmapOverhead = 4096;
                    if ((ulong)bitmap.Height > ((ulong)MaximumBackupBytes - bitmapOverhead) / stride) return 0;
                    return stride * (ulong)bitmap.Height + bitmapOverhead;
                }
                if (format == Palette) {
                    uint entries;
                    if (GetPaletteObject(handle, sizeof(ushort), out entries) != sizeof(ushort)) return 0;
                    entries &= ushort.MaxValue;
                    if (entries == 0 || entries > 1024) return 0;
                    return 4 + (ulong)entries * 4;
                }
                if (format == EnhancedMetafile) return GetEnhMetaFileBits(handle, 0, IntPtr.Zero);
                if (format == MetafilePicture) {
                    ulong structureBytes = GlobalSize(handle).ToUInt64();
                    if (structureBytes < (ulong)Marshal.SizeOf(typeof(MetafilePictureObject)) || structureBytes >= (ulong)MaximumBackupBytes) return 0;
                    var metafile = GetMetafilePictureHandle(handle);
                    uint metafileBytes = metafile == IntPtr.Zero ? 0 : GetMetaFileBitsEx(metafile, 0, IntPtr.Zero);
                    return metafileBytes == 0 ? 0 : structureBytes + metafileBytes;
                }
                return GlobalSize(handle).ToUInt64();
            }

            static IntPtr GetMetafilePictureHandle(IntPtr handle) {
                IntPtr data = GlobalLock(handle);
                if (data == IntPtr.Zero) return IntPtr.Zero;
                try { return Marshal.PtrToStructure<MetafilePictureObject>(data).Metafile; }
                finally { GlobalUnlock(handle); }
            }

            static bool SameFormats(List<uint> first, List<uint> second) {
                if (first.Count != second.Count) return false;
                for (int i = 0; i < first.Count; i++) if (first[i] != second[i]) return false;
                return true;
            }

            // False means the clipboard is busy and no data was read or replaced.
            internal bool TryReadAndRestore(IntPtr clipboardWindow, ClipboardStamp copy, uint sourceProcess, Func<bool> shouldReturnText, out string text) {
                text = null;
                if (!OpenClipboard(clipboardWindow)) return false;
                string outcome = null;
                try {
                    if (!ClipboardStamp.Read().Equals(copy) || copy.Equals(Stamp) || WindowProcess(copy.Owner) != sourceProcess) {
                        outcome = "copy-clipboard-changed-before-read";
                        return true;
                    }
                    bool active = false;
                    string copied = null;
                    bool restored = false;
                    try {
                        active = shouldReturnText();
                        // Requesting a delayed format can establish freshness while holding the lock.
                        if (active || copy.Sequence == Stamp.Sequence) {
                            IntPtr handle = GetClipboardData(UnicodeText);
                            if (active) copied = ReadUnicodeText(handle);
                        }
                    } finally {
                        var rendered = ClipboardStamp.Read();
                        if (rendered.Owner == copy.Owner && rendered.Sequence != Stamp.Sequence && WindowProcess(rendered.Owner) == sourceProcess) {
                            restored = Restore();
                            if (!restored) outcome = "copy-restore-failed";
                        } else outcome = "copy-output-freshness-unconfirmed";
                    }
                    if (restored) {
                        if (!active || !shouldReturnText()) outcome = "copy-restored-after-cancellation-or-timeout";
                        else {
                            text = copied;
                            outcome = text == null ? "copy-unicode-unavailable" : "copy-captured";
                        }
                    }
                    return true;
                } finally {
                    if (CloseClipboard() && outcome != null) SelectionDiagnostics.Record(outcome);
                }
            }

            bool Restore() {
                if (!EmptyClipboard()) return false;
                bool complete = true;
                foreach (var entry in entries) {
                    if (SetClipboardData(entry.Format, entry.Handle) != IntPtr.Zero) {
                        entry.Handle = IntPtr.Zero;
                    } else {
                        complete = false;
                        Trace.TraceError("Could not restore clipboard format {0} (Win32 error {1}).", entry.Format, Marshal.GetLastWin32Error());
                    }
                }
                return complete;
            }

            public void Dispose() {
                foreach (var entry in entries) {
                    if (entry.Handle == IntPtr.Zero) continue;
                    if (entry.Format == Bitmap || entry.Format == Palette) DeleteObject(entry.Handle);
                    else if (entry.Format == EnhancedMetafile) DeleteEnhMetaFile(entry.Handle);
                    else {
                        if (entry.Format == MetafilePicture) {
                            var metafile = GetMetafilePictureHandle(entry.Handle);
                            if (metafile != IntPtr.Zero) DeleteMetaFile(metafile);
                        }
                        GlobalFree(entry.Handle);
                    }
                    entry.Handle = IntPtr.Zero;
                }
            }
        }

        [StructLayout(LayoutKind.Sequential)] struct GuiThreadInfo {
            internal uint Size, Flags;
            internal IntPtr Active, Focus, Capture, MenuOwner, MoveSize, Caret;
            internal int Left, Top, Right, Bottom;
        }

        [StructLayout(LayoutKind.Sequential)] struct BitmapObject {
            internal int Type, Width, Height, Stride;
            internal ushort Planes, BitsPerPixel;
            internal IntPtr Pixels;
        }

        [StructLayout(LayoutKind.Sequential)] struct MetafilePictureObject {
            internal int MappingMode, Width, Height;
            internal IntPtr Metafile;
        }

        [StructLayout(LayoutKind.Sequential)] struct Input {
            internal uint Type;
            internal InputValue Value;

            internal static Input Key(int key, bool release) {
                return new Input { Type = 1, Value = new InputValue { Keyboard = new KeyboardInput { Key = (ushort)key, Flags = release ? 2u : 0u } } };
            }
        }

        [StructLayout(LayoutKind.Explicit)] struct InputValue {
            [FieldOffset(0)] internal KeyboardInput Keyboard;
            [FieldOffset(0)] internal MouseInput Mouse;
        }

        [StructLayout(LayoutKind.Sequential)] struct KeyboardInput {
            internal ushort Key, Scan;
            internal uint Flags, Time;
            internal UIntPtr Extra;
        }

        [StructLayout(LayoutKind.Sequential)] struct MouseInput {
            internal int X, Y;
            internal uint Data, Flags, Time;
            internal UIntPtr Extra;
        }

        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
        [DllImport("user32.dll")] static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr window, StringBuilder name, int size);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] static extern int GetWindowLong(IntPtr window, int index);
        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);
        [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint count, Input[] keys, int size);
        [DllImport("user32.dll")] static extern bool OpenClipboard(IntPtr window);
        [DllImport("user32.dll")] static extern bool CloseClipboard();
        [DllImport("user32.dll")] static extern bool EmptyClipboard();
        [DllImport("user32.dll")] static extern uint GetClipboardSequenceNumber();
        [DllImport("user32.dll")] static extern IntPtr GetClipboardOwner();
        [DllImport("user32.dll")] static extern IntPtr GetOpenClipboardWindow();
        [DllImport("user32.dll", SetLastError = true)] static extern uint EnumClipboardFormats(uint format);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClipboardFormatName(uint format, StringBuilder name, int size);
        [DllImport("user32.dll")] static extern bool IsClipboardFormatAvailable(uint format);
        [DllImport("user32.dll")] static extern IntPtr GetClipboardData(uint format);
        [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetClipboardData(uint format, IntPtr data);
        [DllImport("kernel32.dll")] static extern uint GetCurrentProcessId();
        [DllImport("kernel32.dll")] static extern void SetLastError(uint error);
        [DllImport("kernel32.dll")] static extern UIntPtr GlobalSize(IntPtr handle);
        [DllImport("kernel32.dll")] static extern IntPtr GlobalLock(IntPtr handle);
        [DllImport("kernel32.dll")] static extern bool GlobalUnlock(IntPtr handle);
        [DllImport("kernel32.dll")] static extern IntPtr GlobalFree(IntPtr handle);
        [DllImport("gdi32.dll", EntryPoint = "GetObjectW")] static extern int GetBitmapObject(IntPtr handle, int size, out BitmapObject bitmap);
        [DllImport("gdi32.dll", EntryPoint = "GetObjectW")] static extern int GetPaletteObject(IntPtr handle, int size, out uint entries);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr handle);
        [DllImport("gdi32.dll")] static extern uint GetMetaFileBitsEx(IntPtr metafile, uint size, IntPtr data);
        [DllImport("gdi32.dll")] static extern uint GetEnhMetaFileBits(IntPtr metafile, uint size, IntPtr data);
        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CopyEnhMetaFile(IntPtr metafile, string filename);
        [DllImport("gdi32.dll")] static extern bool DeleteMetaFile(IntPtr metafile);
        [DllImport("gdi32.dll")] static extern bool DeleteEnhMetaFile(IntPtr metafile);
        [DllImport("ole32.dll")] static extern IntPtr OleDuplicateData(IntPtr source, ushort format, uint flags);
        [DllImport("ole32.dll")] static extern int OleInitialize(IntPtr reserved);
        [DllImport("ole32.dll")] static extern void OleUninitialize();
        [DllImport("ole32.dll")] static extern int OleGetClipboard([MarshalAs(UnmanagedType.Interface)] out IDataObject data);
    }
}
