namespace BlitzCS.BinaryCompiler.Runtime;

/// <summary>A guest software exception (RaiseException) reached the end of the FS:[0] registration chain without being handled. Windows would end the process.</summary>
public sealed class GuestUnhandledException : GuestException
{
    public GuestUnhandledException(uint code, uint flags, uint address, uint[] parameters)
        : base($"Unhandled guest exception 0x{code:X8}{Describe(code)} raised at 0x{address:X8} (flags 0x{flags:X}): no handler on the FS:[0] registration chain handled it (the chain ended at 0xFFFFFFFF; Windows would terminate the process).")
    {
        Code = code; Flags = flags; Address = address; Parameters = parameters;
    }

    public uint Code { get; }
    public uint Flags { get; }
    /// <summary>EXCEPTION_RECORD.ExceptionAddress: the guest return address of the RaiseException call.</summary>
    public uint Address { get; }
    public IReadOnlyList<uint> Parameters { get; }

    private static string Describe(uint code) => code switch
    {
        0xE06D7363 => " (MSVC C++ exception)",
        0xC0000025 => " (STATUS_NONCONTINUABLE_EXCEPTION)",
        0xC0000027 => " (STATUS_UNWIND)",
        _ => "",
    };
}

/// <summary>Guest structured exception handling reached a case the dispatcher does not support or the guest misused it (named, never a silent stub).</summary>
public sealed class GuestSehException : GuestException
{
    public GuestSehException(string message) : base(message) { }
}

/// <summary>
/// Guest structured exception handling (docs/binary-seh.md): kernel32 <c>RaiseException</c> and <c>RtlUnwind</c> for x86 frame-based SEH over the FS:[0] chain.
/// Both are stdcall with four arguments; bind them as imports with <see cref="RaiseExceptionImport"/> and <see cref="RtlUnwindImport"/> (the Win32 component does).
/// </summary>
public static class GuestStructuredExceptions
{
    /// <summary>KERNEL32!RaiseException(code, flags, count, arguments).</summary>
    public static uint RaiseException(GuestCall call) => GuestModule.SehOwner().RaiseExceptionCore(call);

    /// <summary>KERNEL32!RtlUnwind(targetFrame, targetIp, exceptionRecord, returnValue).</summary>
    public static uint RtlUnwind(GuestCall call) => GuestModule.SehOwner().RtlUnwindCore(call);

    public static GuestImport RaiseExceptionImport() => GuestImportBuilder.StdcallCall(4, RaiseException);
    public static GuestImport RtlUnwindImport() => GuestImportBuilder.StdcallCall(4, RtlUnwind);
}

/// <summary>
/// The dispatcher. It is continuation-passing: a handler is not called from managed code, the import stub is redirected into it (ImportStubLayout.RedirectFlag) with a
/// return address that is a runtime stub of the module; when the handler returns there, the dispatch continues. All dispatcher state lives in a block on the guest
/// stack below the RaiseException / RtlUnwind call, exactly where Windows keeps its own frames, so a handler that never returns (an MSVC catch transferring control
/// to the continuation with <c>mov esp / jmp</c>) leaves nothing behind on the native side: the guest simply runs on in the same guest call.
/// </summary>
public sealed unsafe partial class GuestModule
{
    private const uint ChainEnd = 0xFFFFFFFF;
    private const uint EhNonContinuable = 1, EhUnwinding = 2, EhExitUnwind = 4, EhNestedCall = 0x10;
    private const uint ContinueExecution = 0, ContinueSearch = 1, NestedException = 2, CollidedUnwind = 3;
    private const uint StatusUnwind = 0xC0000027;
    private const uint ContextI386Control = 0x10001, ContextI386Integer = 0x10002;
    private const uint BlockMagic = 0x48455342, DispatchTag = 0x50534944, UnwindTag = 0x444E5755;
    private const uint KindDispatch = 1, KindUnwind = 2;
    // Dispatcher block on the guest stack (offsets from its base B). A handler is entered with ESP = B - 20 (return address + four arguments), so after a stdcall
    // handler returns ESP = B and after a cdecl one ESP = B - 16: the runtime finds B from either through BMagic/BSelf.
    private const uint BMagic = 0x00, BSelf = 0x04, BKind = 0x08, BCurrent = 0x0C, BNestedFrame = 0x10, BCallerEsp = 0x14,
        BFrame = 0x18 /* extended registration record: Next, Handler, Tag, the record whose handler runs */, BDispatcherContext = 0x28, BTarget = 0x2C,
        BReturnValue = 0x30, BRecordPtr = 0x34, BSavedEbx = 0x38, BSavedEbp = 0x3C, BSavedEsi = 0x40, BSavedEdi = 0x44, BImage = 0x48 /* 44 bytes */,
        BRecord = 0x80 /* EXCEPTION_RECORD, 0x50 bytes */, BContext = 0xD0 /* CONTEXT, 0x2CC bytes */, BlockSize = 0x3A0;
    private const uint CtxEdiOffset = 0x9C; // CONTEXT.Edi: the register image of a redirect starts here
    private const int MaximumParameters = 15;

    private GuestImport? sehContinueImport, sehFrameImport;

    private void InitializeRuntimeStubs()
    {
        if (artifact.RuntimeStubs == 0) return;
        sehContinueImport = GuestImport.Create(c => SehOwner().SehContinue(c), 0);
        sehFrameImport = GuestImport.Create(c => SehOwner().SehFrameHandler(c), 0);
        var first = artifact.Imports.Count + artifact.DynamicStubs;
        *(nint*)(hostMap + (nint)artifact.ImportThunkTableOffset + 8 * first) = sehContinueImport.FunctionPointer;
        *(nint*)(hostMap + (nint)artifact.ImportThunkTableOffset + 8 * (first + 1)) = sehFrameImport.FunctionPointer;
    }

    private uint RuntimeStubAddress(int k) => artifact.GuestImageBase + artifact.ImportStubBaseRva + 16u * (uint)(artifact.Imports.Count + artifact.DynamicStubs + k);

    /// <summary>The module whose guest call is running on this thread (its call context holds the registers the stub recorded).</summary>
    internal static GuestModule SehOwner()
    {
        var module = current ?? throw new InvalidOperationException("Guest SEH imports may only run inside a guest call.");
        if (module.artifact.RuntimeStubs == 0)
            throw new GuestSehException("The running module was translated without runtime stubs (artifact format before 6 or a hand-built artifact), so guest exception dispatch is not available. Re-translate the image.");
        return module;
    }

    private ThreadState SehState()
    {
        var st = threadState.Value!;
        if (st.CallContext == null || st.Teb is null) throw new InvalidOperationException("Guest SEH imports may only run inside a guest call.");
        return st;
    }

    private static uint Captured(ThreadState st, int offset) => *(uint*)(st.CallContext + offset);
    private uint Fs0(ThreadState st) => *(uint*)(st.Teb!.Map + (nint)TebExceptionList);
    private void SetFs0(ThreadState st, uint value) => *(uint*)(st.Teb!.Map + (nint)TebExceptionList) = value;
    private uint R(uint address) => Memory.ReadUInt32(address);
    private void W(uint address, uint value) => Memory.WriteUInt32(address, value);

    private void Redirect(ThreadState st, uint image)
    {
        st.ExtRedirect = image;
        st.ExtFlags |= ImportStubLayout.RedirectFlag;
    }

    /// <summary>Reserves the dispatcher block below the caller's frame and records the caller's callee-saved registers (what the import stub stored).</summary>
    private uint AllocateBlock(ThreadState st, uint callerEsp, uint kind)
    {
        var b = (callerEsp - 64 - BlockSize) & ~15u;
        if (callerEsp > st.CallTop || callerEsp < st.CallLow || b - 20 < st.CallLow + (uint)options.StackReserve)
            throw new GuestStackException($"Guest exception dispatch at ESP 0x{callerEsp:X8} has no room for its 0x{BlockSize:X}-byte frame above the stack reserve of the current guest call (stack limit 0x{st.CallLow:X8}).");
        Memory.Write(b, new byte[BlockSize]);
        W(b + BMagic, BlockMagic); W(b + BSelf, b); W(b + BKind, kind); W(b + BCallerEsp, callerEsp);
        W(b + BSavedEbx, Captured(st, ImportStubLayout.CtxGuestEbx)); W(b + BSavedEbp, Captured(st, ImportStubLayout.CtxGuestEbp));
        W(b + BSavedEsi, Captured(st, ImportStubLayout.CtxGuestEsi)); W(b + BSavedEdi, Captured(st, ImportStubLayout.CtxGuestEdi));
        return b;
    }

    /// <summary>The CONTEXT handed to handlers: the state right after the call returned (Eip = return address, Esp above the four stdcall arguments) with the caller's callee-saved registers.</summary>
    private void WriteContext(uint b, uint eip, uint esp, uint eax)
    {
        var c = b + BContext;
        W(c, ContextI386Control | ContextI386Integer);
        var r = c + CtxEdiOffset;
        W(r + ImportStubLayout.ImageEdi, R(b + BSavedEdi)); W(r + ImportStubLayout.ImageEsi, R(b + BSavedEsi)); W(r + ImportStubLayout.ImageEbx, R(b + BSavedEbx));
        W(r + ImportStubLayout.ImageEax, eax); W(r + ImportStubLayout.ImageEbp, R(b + BSavedEbp)); W(r + ImportStubLayout.ImageEip, eip); W(r + ImportStubLayout.ImageEsp, esp);
    }

    private void CheckRecord(ThreadState st, uint rn, string phase)
    {
        if (rn % 4 == 0 && rn >= st.CallLow && (ulong)rn + 8 <= st.CallTop) return;
        if (rn >= st.CallTop && rn % 4 == 0 && IsMappedGuest(rn))
            throw new GuestSehException($"Guest {phase} reached the registration record 0x{rn:X8}, which lies above the frame of the innermost guest call (0x{st.CallTop:X8}): the exception would have to cross a host (managed) frame between two guest calls, which guest SEH does not support.");
        throw new GuestSehException($"Guest {phase} found an invalid registration record 0x{rn:X8} on the FS:[0] chain (not a 4-byte aligned record inside the guest stack of the current call, 0x{st.CallLow:X8}..0x{st.CallTop:X8}).");
    }

    private bool IsMappedGuest(uint address) { try { return Memory.IsMapped(address, 8); } catch (GuestException) { return false; } }

    /// <summary>Enters <paramref name="handler"/>(record, frame, context, dispatcherContext) with the dispatcher's continuation stub as its return address.</summary>
    private uint CallHandler(ThreadState st, uint b, uint handler, uint record, uint frame)
    {
        var f = b - 20;
        W(f, RuntimeStubAddress(0)); W(f + 4, record); W(f + 8, frame); W(f + 12, b + BContext); W(f + 16, b + BDispatcherContext);
        var image = b + BImage;
        W(image + ImportStubLayout.ImageEdi, R(b + BSavedEdi)); W(image + ImportStubLayout.ImageEsi, R(b + BSavedEsi)); W(image + ImportStubLayout.ImageEbx, R(b + BSavedEbx));
        W(image + ImportStubLayout.ImageEdx, 0); W(image + ImportStubLayout.ImageEcx, 0); W(image + ImportStubLayout.ImageEax, 0);
        W(image + ImportStubLayout.ImageEbp, R(b + BSavedEbp)); W(image + ImportStubLayout.ImageEip, handler); W(image + ImportStubLayout.ImageEsp, f);
        Redirect(st, image);
        return 0;
    }

    /// <summary>Installs the dispatcher's own extended registration record (as Windows does around every handler call) so that MSVC's _UnwindNestedFrames / _JumpToContinuation find it at FS:[0].</summary>
    private void PushFrame(ThreadState st, uint b, uint tag, uint rn)
    {
        var frame = b + BFrame;
        W(frame, Fs0(st)); W(frame + 4, RuntimeStubAddress(1)); W(frame + 8, tag); W(frame + 12, rn);
        SetFs0(st, frame);
        W(b + BCurrent, rn); W(b + BDispatcherContext, 0);
    }

    // ---- RaiseException ----

    internal uint RaiseExceptionCore(GuestCall call)
    {
        var st = SehState();
        var e = call.Esp;
        var ret = R(e);
        var code = call.Arg(0); var flags = call.Arg(1) & EhNonContinuable; var count = call.Arg(2); var args = call.Arg(3);
        var n = args == 0 ? 0u : Math.Min(count, (uint)MaximumParameters);
        var b = AllocateBlock(st, e, KindDispatch);
        var rec = b + BRecord;
        W(rec, code); W(rec + 4, flags); W(rec + 8, 0); W(rec + 12, ret); W(rec + 16, n);
        for (var i = 0u; i < n; i++) W(rec + 20 + 4 * i, R(args + 4 * i));
        W(b + BRecordPtr, rec);
        WriteContext(b, ret, e + 20, 0);
        return SearchNext(st, b, Fs0(st));
    }

    /// <summary>One step of RtlDispatchException: call the handler of <paramref name="rn"/>, or end the search.</summary>
    private uint SearchNext(ThreadState st, uint b, uint rn)
    {
        var rec = b + BRecord;
        if (rn == ChainEnd)
        {
            var n = Math.Min(R(rec + 16), (uint)MaximumParameters);
            var p = new uint[n];
            for (var i = 0u; i < n; i++) p[i] = R(rec + 20 + 4 * i);
            throw new GuestUnhandledException(R(rec), R(rec + 4), R(rec + 12), p);
        }
        CheckRecord(st, rn, "exception dispatch");
        var handler = R(rn + 4);
        PushFrame(st, b, DispatchTag, rn);
        return CallHandler(st, b, handler, rec, rn);
    }

    // ---- RtlUnwind ----

    internal uint RtlUnwindCore(GuestCall call)
    {
        var st = SehState();
        var e = call.Esp;
        var ret = R(e);
        var target = call.Arg(0); var recPtr = call.Arg(2); var retval = call.Arg(3);   // TargetIp (Arg 1) is not used by x86 RtlUnwind: control returns to the caller
        if (target == 0)
            throw new GuestSehException($"RtlUnwind with TargetFrame = NULL (an exit unwind) called from 0x{ret:X8} is not supported by guest SEH.");
        var b = AllocateBlock(st, e, KindUnwind);
        if (recPtr == 0)
        {
            recPtr = b + BRecord;
            W(recPtr, StatusUnwind); W(recPtr + 4, 0); W(recPtr + 8, 0); W(recPtr + 12, ret); W(recPtr + 16, 0);
        }
        W(recPtr + 4, R(recPtr + 4) | EhUnwinding);   // the caller's record is updated in place, as on Windows
        W(b + BRecordPtr, recPtr); W(b + BTarget, target); W(b + BReturnValue, retval);
        WriteContext(b, ret, e + 20, retval);
        return UnwindNext(st, b, Fs0(st));
    }

    private uint UnwindNext(ThreadState st, uint b, uint rn)
    {
        var target = R(b + BTarget);
        if (rn == target || (rn == ChainEnd && target == ChainEnd))
        {
            Redirect(st, b + BContext + CtxEdiOffset);   // return to RtlUnwind's caller: EAX = ReturnValue, callee-saved registers as at the call
            return 0;
        }
        if (rn == ChainEnd)
            throw new GuestSehException($"RtlUnwind target frame 0x{target:X8} is not on the FS:[0] chain (the chain ended at 0xFFFFFFFF); Windows raises the exception again and the process ends.");
        if (target != ChainEnd && target < rn)
            throw new GuestSehException($"RtlUnwind target frame 0x{target:X8} lies below the registration record 0x{rn:X8} it would have to unwind (STATUS_INVALID_UNWIND_TARGET).");
        CheckRecord(st, rn, "unwind");
        var handler = R(rn + 4);
        PushFrame(st, b, UnwindTag, rn);
        return CallHandler(st, b, handler, R(b + BRecordPtr), rn);
    }

    // ---- runtime stubs ----

    /// <summary>Runtime stub 0: a handler called by the dispatcher returned (EAX = its EXCEPTION_DISPOSITION).</summary>
    private uint SehContinue(GuestCall call)
    {
        var st = SehState();
        var esp = call.Esp;
        uint b;
        if (R(esp + BMagic) == BlockMagic && R(esp + BSelf) == esp) b = esp;                          // stdcall handler
        else if (R(esp + 16 + BMagic) == BlockMagic && R(esp + 16 + BSelf) == esp + 16) b = esp + 16; // cdecl handler (MSVC's are cdecl)
        else throw new GuestSehException($"The guest returned to the SEH dispatcher continuation with ESP 0x{esp:X8}, which matches no dispatcher frame (a handler did not return with the stack of a cdecl or stdcall call of four arguments).");
        var disposition = Captured(st, ImportStubLayout.CtxGuestEax);
        var frame = b + BFrame;
        if (Fs0(st) != frame)
            throw new GuestSehException($"A guest exception handler returned with FS:[0] = 0x{Fs0(st):X8} instead of the dispatcher's own record 0x{frame:X8} (it left registration records installed).");
        SetFs0(st, R(frame));
        var rn = R(b + BCurrent);
        if (R(b + BKind) == KindDispatch)
        {
            var rec = b + BRecord;
            var flags = R(rec + 4);
            if (rn == R(b + BNestedFrame)) { flags &= ~EhNestedCall; W(rec + 4, flags); W(b + BNestedFrame, 0); }
            switch (disposition)
            {
                case ContinueExecution:
                    if ((flags & EhNonContinuable) != 0)
                        throw new GuestSehException($"The handler 0x{R(rn + 4):X8} of record 0x{rn:X8} returned ExceptionContinueExecution for the non-continuable exception 0x{R(rec):X8} (Windows raises STATUS_NONCONTINUABLE_EXCEPTION; not supported).");
                    Redirect(st, b + BContext + CtxEdiOffset);   // resume with the (possibly edited) CONTEXT
                    return 0;
                case ContinueSearch:
                    return SearchNext(st, b, R(rn));
                case NestedException:
                    W(rec + 4, flags | EhNestedCall);
                    var nested = R(b + BDispatcherContext);
                    if (nested > R(b + BNestedFrame)) W(b + BNestedFrame, nested);
                    return SearchNext(st, b, R(rn));
                default:
                    throw new GuestSehException($"The handler 0x{R(rn + 4):X8} of record 0x{rn:X8} returned the invalid disposition {disposition} during exception dispatch (STATUS_INVALID_DISPOSITION).");
            }
        }
        switch (disposition)
        {
            case ContinueSearch:
                var next = R(rn);
                SetFs0(st, next);   // unlink the unwound record
                return UnwindNext(st, b, next);
            case CollidedUnwind:
                throw new GuestSehException($"The handler 0x{R(rn + 4):X8} of record 0x{rn:X8} reported a collided unwind (an unwind started inside an unwind handler); not supported by guest SEH.");
            default:
                throw new GuestSehException($"The handler 0x{R(rn + 4):X8} of record 0x{rn:X8} returned the invalid disposition {disposition} during unwind (STATUS_INVALID_DISPOSITION).");
        }
    }

    /// <summary>
    /// Runtime stub 1: the handler of the dispatcher's own registration records (cdecl, four arguments). A dispatch record seen by a new search means an exception
    /// was raised inside a handler (ExceptionNestedException, DispatcherContext = the record whose handler was running); seen by an unwind it is skipped. An unwind
    /// record seen by another unwind is a collided unwind (named fault); seen by a search it is skipped.
    /// </summary>
    private uint SehFrameHandler(GuestCall call)
    {
        var record = call.Arg(0); var establisher = call.Arg(1); var dispatcherContext = call.Arg(3);
        var unwinding = (R(record + 4) & (EhUnwinding | EhExitUnwind)) != 0;
        switch (R(establisher + 8))
        {
            case DispatchTag:
                if (unwinding) return ContinueSearch;
                W(dispatcherContext, R(establisher + 12));
                return NestedException;
            case UnwindTag:
                if (unwinding)
                    throw new GuestSehException($"A second unwind reached the record 0x{establisher:X8} of an unwind in progress (collided unwind); not supported by guest SEH.");
                return ContinueSearch;
            default:
                throw new GuestSehException($"The guest SEH frame handler was called for 0x{establisher:X8}, which is not a dispatcher registration record.");
        }
    }
}
