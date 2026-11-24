using System.IO;
using System.Windows.Threading;
using Drilling.Common.Managers;
using Drilling.Common.Review;
using Drilling.UI.Menu.Menus;

internal static partial class Program
{
    // Deliberately gated synchronous store entry tests scheduling/ordering, not speed.
    // They must never be presented as real disk/vision performance improvements.
    private static async Task RunNativeIoSchedulingChecks()
    {
        static (Fixture Fixture,SchedulingOffsets Store,CManualInspectionBackend Backend) Setup()
        {
            var f=new Fixture();var store=new SchedulingOffsets(f.Offsets);var owner=Dispatcher.FromThread(Thread.CurrentThread);
            return(f,store,new(f.Review,new CRecipeManager(new RecipesFile(f)),f.Settings,f.Results,store,()=>{if(owner is not null&&!owner.CheckAccess())store.SelectionOutsideOwner=true;return f.Selected;},null,()=>{}));
        }
        await Check("native_load_synchronous_offset_store_entry_runs_off_dispatcher_and_keeps_input_alive",()=>MonitorSta(async()=>
        {
            var(f,store,backend)=Setup();store.Arm();var ui=Environment.CurrentManagedThreadId;
            Task<CManualInspectionSource>? pending=null;
            try
            {
                pending=backend.LoadAsync(null,default);await store.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var pulse=false;await Dispatcher.CurrentDispatcher.InvokeAsync(()=>pulse=true,DispatcherPriority.Input);
                True(pulse&&!pending.IsCompleted&&store.ReadThreadId!=ui);
            }
            finally {store.Release.Set();}
            var loaded=await pending!;True(loaded.Context.RecipeId=="QA"&&loaded.Points.Count>0&&store.Writes==0&&!store.SelectionOutsideOwner);
        }));
        await Check("native_ensure_current_synchronous_offset_entry_does_not_block_dispatcher",()=>MonitorSta(async()=>
        {
            var(f,store,backend)=Setup();var source=await backend.LoadAsync(null,default);store.Arm();var ui=Environment.CurrentManagedThreadId;
            Task? pending=null;
            try
            {
                pending=backend.EnsureCurrentAsync(source,default);await store.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var pulse=false;await Dispatcher.CurrentDispatcher.InvokeAsync(()=>pulse=true,DispatcherPriority.Input);
                True(pulse&&!pending.IsCompleted&&store.ReadThreadId!=ui);
            }
            finally {store.Release.Set();}
            await pending!;True(store.Writes==0&&!store.SelectionOutsideOwner);
        }));
        await Check("native_ensure_current_cancelled_during_worker_io_propagates_without_write_or_cached_success",()=>MonitorSta(async()=>
        {
            var(f,store,backend)=Setup();var source=await backend.LoadAsync(null,default);store.Arm();using var cancel=new CancellationTokenSource();
            var pending=backend.EnsureCurrentAsync(source,cancel.Token);
            try
            {
                await store.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));cancel.Cancel();
                var cancelled=false;try{await pending;}catch(OperationCanceledException){cancelled=true;}True(cancelled&&store.Writes==0);
            }
            finally {store.Release.Set();}
            store.Disarm();await backend.EnsureCurrentAsync(source,default);True(store.Writes==0);
        }));
        await Check("native_ensure_current_rechecks_selected_recipe_after_off_thread_file_read",()=>MonitorSta(async()=>
        {
            var(f,store,backend)=Setup();var source=await backend.LoadAsync(null,default);store.Arm();var pending=backend.EnsureCurrentAsync(source,default);
            try {await store.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));f.Selected="OTHER";}
            finally {store.Release.Set();}
            await Reject(()=>pending);True(store.Writes==0);f.Selected="QA";store.Disarm();await backend.EnsureCurrentAsync(source,default);
        }));
        await Check("native_worker_io_error_is_not_cached_and_same_path_same_stamp_changed_sha_stays_stale",async()=>
        {
            var(f,store,backend)=Setup();var saved=await f.Offsets.SaveNew("QA",[new(1,"A1",.002,.003){IsDesignCoordinate=true}]);var source=await backend.LoadAsync(null,default);
            store.Fault=new IOException("synthetic synchronous store read failure");var failed=false;
            try{await backend.EnsureCurrentAsync(source,default);}catch(IOException error){failed=error.Message.Contains("synthetic synchronous");}True(failed&&store.Writes==0);
            store.Fault=null;await backend.EnsureCurrentAsync(source,default);
            var bytes=await System.IO.File.ReadAllBytesAsync(saved.FilePath);var original=System.Text.Encoding.UTF8.GetString(bytes);var changed=original.Replace("0.002000","0.004000");var replacement=System.Text.Encoding.UTF8.GetBytes(changed);
            True(changed!=original&&replacement.Length==bytes.Length); // Preserve an existing BOM and byte length, too.
            var stamp=System.IO.File.GetLastWriteTimeUtc(saved.FilePath);await System.IO.File.WriteAllBytesAsync(saved.FilePath,replacement);System.IO.File.SetLastWriteTimeUtc(saved.FilePath,stamp);
            await Reject(()=>backend.EnsureCurrentAsync(source,default));True(store.Writes==0);
        });
    }
    private sealed class SchedulingOffsets(CReviewOffsetFileBase inner):CReviewOffsetFileBase
    {
        public TaskCompletionSource Entered{get;private set;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly ManualResetEventSlim Release=new(false);public int ReadThreadId,Writes;public bool SelectionOutsideOwner;public Exception? Fault;private volatile bool armed;
        public void Arm(){Release.Reset();Entered=new(TaskCreationOptions.RunContinuationsAsynchronously);armed=true;}
        public void Disarm(){armed=false;Release.Set();}
        public override string RootPath=>inner.RootPath;
        public override Task<ST_REVIEW_OFFSET_FILE_DATA> LoadLatest(string id,CancellationToken token=default)
        {
            token.ThrowIfCancellationRequested();if(Fault is { } fault)throw fault;
            if(armed){ReadThreadId=Environment.CurrentManagedThreadId;Entered.TrySetResult();if(!Release.Wait(TimeSpan.FromSeconds(5),token))throw new TimeoutException("Synchronous read gate was not released; dispatcher remained blocked.");}
            return inner.LoadLatest(id,token);
        }
        public override Task<ST_REVIEW_OFFSET_FILE_DATA> SaveNew(string id,IReadOnlyList<ST_REVIEW_OFFSET_ROW> rows,CancellationToken token=default){Writes++;throw new NotSupportedException();}
        public override Task<ST_REVIEW_OFFSET_SOURCE_APPLICATION?> FindSourceApplication(string id,string path,CancellationToken token=default)=>inner.FindSourceApplication(id,path,token);
        public override Task<ST_REVIEW_OFFSET_SOURCE_APPLICATION?> FindCommittedSourceApplication(string id,string path,CancellationToken token=default)=>inner.FindCommittedSourceApplication(id,path,token);
        public override Task<ST_REVIEW_OFFSET_FILE_DATA> SaveNewFromSource(string id,IReadOnlyList<ST_REVIEW_OFFSET_ROW> rows,string path,bool duplicate,CancellationToken token=default){Writes++;throw new NotSupportedException();}
        public override Task<ST_REVIEW_OFFSET_ROLLBACK_RESULT> RollbackLatest(string id,string path,CancellationToken token=default){Writes++;throw new NotSupportedException();}
    }
}
