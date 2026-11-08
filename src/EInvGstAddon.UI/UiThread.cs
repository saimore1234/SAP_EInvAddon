using System;
using System.Threading;
using System.Windows.Forms;
using EInvGstAddon.Common;
using EInvGstAddon.Core;

namespace EInvGstAddon.UI
{
    /// <summary>
    /// SAP UI/DI API objects belong to the main STA thread. Event handlers return quickly; anything else is posted here.
    /// Worker threads (HTTP to ERPNext) reach SAP only through <see cref="Send{T}"/> / <see cref="Post"/>.
    /// </summary>
    public sealed class UiThread : IUiInvoker
    {
        private static SynchronizationContext _ctx;
        private static int _threadId;

        public static void Capture()
        {
            if (SynchronizationContext.Current == null)
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
            _ctx = SynchronizationContext.Current;
            _threadId = Thread.CurrentThread.ManagedThreadId;
        }

        public static bool IsUiThread { get { return Thread.CurrentThread.ManagedThreadId == _threadId; } }

        public static T Send<T>(Func<T> f)
        {
            if (IsUiThread) return f();
            T result = default(T);
            Exception err = null;
            _ctx.Send(_ => { try { result = f(); } catch (Exception ex) { err = ex; } }, null);
            if (err != null) throw err;
            return result;
        }

        public static void Post(Action a, string what)
        {
            _ctx.Post(_ =>
            {
                try { a(); }
                catch (Exception ex) { Logger.Error("ui.post.failed", ex, "what", what); }
            }, null);
        }

        public T Invoke<T>(Func<T> f) { return Send(f); }
    }
}
