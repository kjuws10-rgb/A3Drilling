using System.IO;
using System.Reflection;
using Drilling.Common.Managers;
using Drilling.Common.Recipe;
using Drilling.Common.Review;
using Drilling.UI.Menu.Menus;

internal static partial class Program
{
    private static async Task RunRecipeDisplayChecks()
    {
        await Check("recipe_saved_review_overlay_uses_real_planner_and_never_changes_editor_or_save_input",async()=>
        {
            var f=new RecipeDisplayFixture(); await f.Menu.Build();
            var rows=f.Menu.AllManagedItems.ToArray();
            var before=rows.Select(r=>(r.Key,r.Value,r.OriginalValue,r.IsEdited)).ToArray();
            var saveBefore=RecipeSaveRows(f.Menu);
            f.Store.Put("QA",.012,-.023); await f.Menu.RefreshSavedReviewOffsetPreview();
            AssertRecipePreview(f);
            True(rows.SequenceEqual(f.Menu.AllManagedItems));
            True(before.SequenceEqual(rows.Select(r=>(r.Key,r.Value,r.OriginalValue,r.IsEdited))));
            True(saveBefore.SequenceEqual(RecipeSaveRows(f.Menu)));
            True(!RecipeSaveRows(f.Menu).Any(r=>CReviewOffsetData.IsParameterKey(r.Key)));
        });
        await Check("recipe_live_overlay_tracks_save_undo_redo_and_init_without_recipe_reload",async()=>
        {
            var f=new RecipeDisplayFixture(); await f.Menu.Build();
            var initial=RecipeFinalOffsets(f.Menu);
            foreach(var xy in new[]{(.012,-.023),(.035,.007),(.012,-.023),(.035,.007)})
            {
                f.Store.Put("QA",xy.Item1,xy.Item2); await f.Menu.RefreshSavedReviewOffsetPreview(); AssertRecipePreview(f);
            }
            f.Store.Put("QA",0,0,init:true);await f.Menu.RefreshSavedReviewOffsetPreview();AssertRecipePreview(f);
            True(RecipeFinalOffsets(f.Menu).OrderBy(p=>p.Key).SequenceEqual(initial.OrderBy(p=>p.Key)));
            f.Store.Files["QA"]=ST_REVIEW_OFFSET_FILE_DATA.Empty("QA");
            await f.Menu.RefreshSavedReviewOffsetPreview(); AssertRecipePreview(f);
        });
        await Check("recipe_unchanged_revision_keeps_rows_but_state_only_and_same_path_sha_change_refresh",async()=>
        {
            var f=new RecipeDisplayFixture(); f.Store.Put("QA",0,0); await f.Menu.Build();
            var rows=f.Menu.ShotGroupRows;True(rows.Count>0);
            await f.Menu.RefreshSavedReviewOffsetPreview();True(ReferenceEquals(rows,f.Menu.ShotGroupRows));
            f.Store.Put("QA",0,0,init:true);await f.Menu.RefreshSavedReviewOffsetPreview();
            True(!ReferenceEquals(rows,f.Menu.ShotGroupRows));rows=f.Menu.ShotGroupRows;
            var same=f.Store.Files["QA"];f.Store.Files["QA"]=same with{Rows=[same.Rows[0] with{IsMeasurementOffsetInitialized=false}],ContentSha256="same-path-new-state"};
            await f.Menu.RefreshSavedReviewOffsetPreview();True(!ReferenceEquals(rows,f.Menu.ShotGroupRows));
        });
        await Check("recipe_viewed_model_overlay_is_separate_from_selected_process_model",async()=>
        {
            var f=new RecipeDisplayFixture{View="OTHER",Selected="QA"};
            f.Store.Put("QA",.4,.5);f.Store.Put("OTHER",-.012,.023);
            await f.Menu.Build();AssertRecipePreview(f);
            True(f.Menu.SelectedRecipeFile=="OTHER.csv"&&f.Store.LastRecipe=="OTHER");
            f.Store.Put("QA",.6,.7);var rows=f.Menu.ShotGroupRows;
            await f.Menu.RefreshSavedReviewOffsetPreview();True(ReferenceEquals(rows,f.Menu.ShotGroupRows));
        });
        await Check("recipe_snapshot_is_immutable_model_scoped_and_preserves_protected_offsets",()=>
        {
            var original=new List<ST_REVIEW_OFFSET_ROW>{new(1,"A1",.012,-.023){IsDesignCoordinate=true}};
            var snapshot=CReviewOffsetDisplaySnapshot.Create("QA",new("QA","same","same",DateTimeOffset.UnixEpoch,original));
            original[0]=original[0] with{ReviewOffsetX=9};original.Clear();
            var parameters=new Dictionary<string,string>{{"H01_DEFAULT_OFFSET_X","8"},{"H01_1ST_BEAM_OFFSET_X","7"},{"DISTORTION_BASE_X","6"},{"CELL1_A1_REVIEW_OFFSET_X","5"},{"CELL1_A1_REVIEW_OFFSET_Y","4"}};
            var copy=snapshot.ApplyToCopy(parameters);
            True(copy["CELL1_A1_REVIEW_OFFSET_X"]=="0.012"&&parameters["CELL1_A1_REVIEW_OFFSET_X"]=="5");
            True(copy["H01_DEFAULT_OFFSET_X"]=="8"&&copy["H01_1ST_BEAM_OFFSET_X"]=="7"&&copy["DISTORTION_BASE_X"]=="6");
            copy["CELL1_A1_REVIEW_OFFSET_X"]="99";True(snapshot.ApplyToCopy(parameters)["CELL1_A1_REVIEW_OFFSET_X"]=="0.012");
            try{CReviewOffsetDisplaySnapshot.Create("OTHER",ST_REVIEW_OFFSET_FILE_DATA.Empty("QA"));throw new Exception("Expected model rejection");}catch(InvalidDataException){}
            return Task.CompletedTask;
        });
        await Check("recipe_same_path_without_sha_uses_row_digest_instead_of_missing_update",async()=>
        {
            var f=new RecipeDisplayFixture();f.Store.Put("QA",.012,-.023);f.Store.Files["QA"]=f.Store.Files["QA"] with{ContentSha256=""};
            await f.Menu.Build();var rows=f.Menu.ShotGroupRows;
            var saved=f.Store.Files["QA"];f.Store.Files["QA"]=saved with{Rows=[saved.Rows[0] with{ReviewOffsetX=.031}]};
            await f.Menu.RefreshSavedReviewOffsetPreview();True(!ReferenceEquals(rows,f.Menu.ShotGroupRows));AssertRecipePreview(f);
        });
        await Check("recipe_invalid_or_foreign_saved_file_is_reported_once_without_replacing_valid_preview",async()=>
        {
            var f=new RecipeDisplayFixture();f.Store.Put("QA",.012,-.023);await f.Menu.Build();var rows=f.Menu.ShotGroupRows;
            var valid=f.Store.Files["QA"];
            f.Store.Files["QA"]=valid with{Rows=[valid.Rows[0] with{ReviewOffsetX=double.NaN}],ContentSha256="invalid"};
            await f.Menu.RefreshSavedReviewOffsetPreview();await f.Menu.RefreshSavedReviewOffsetPreview();
            True(ReferenceEquals(rows,f.Menu.ShotGroupRows)&&f.Messages.Count(m=>m.Contains("미리보기 갱신 실패"))==1);
            f.Store.Files["QA"]=valid with{RecipeId="OTHER",ContentSha256="foreign"};
            await f.Menu.RefreshSavedReviewOffsetPreview();await f.Menu.RefreshSavedReviewOffsetPreview();
            True(ReferenceEquals(rows,f.Menu.ShotGroupRows)&&f.Messages.Count(m=>m.Contains("미리보기 갱신 실패"))==2);
            f.Store.Put("QA",.031,.042);await f.Menu.RefreshSavedReviewOffsetPreview();AssertRecipePreview(f);
        });
        await Check("recipe_slow_saved_offset_poll_is_single_flight_and_publishes_once",async()=>
        {
            var f=new RecipeDisplayFixture();await f.Menu.Build();f.Store.Put("QA",.012,-.023);
            var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var reads=f.Store.Reads;
            f.Store.OnRead=async(_,token)=>{entered.SetResult();await release.Task.WaitAsync(token);};
            var first=f.Menu.RefreshSavedReviewOffsetPreview();await entered.Task;
            for(var i=0;i<4;i++) await f.Menu.RefreshSavedReviewOffsetPreview();
            True(f.Store.Reads==reads+1);release.SetResult();await first;AssertRecipePreview(f);
        });
        await Check("recipe_old_poll_cannot_overwrite_new_view_build",async()=>
        {
            var f=new RecipeDisplayFixture();await f.Menu.Build();f.Store.Put("QA",.012,-.023);f.Store.Put("OTHER",.031,.042);
            var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            f.Store.OnRead=async(id,token)=>{if(id=="QA"){entered.TrySetResult();await release.Task.WaitAsync(token);}};
            var old=f.Menu.RefreshSavedReviewOffsetPreview();await entered.Task;
            f.View="OTHER";await f.Menu.Build();var rows=f.Menu.ShotGroupRows;
            release.SetResult();await old;True(ReferenceEquals(rows,f.Menu.ShotGroupRows));AssertRecipePreview(f);
        });
        await Check("recipe_mid_build_model_switch_discards_old_publish_and_next_build_recovers",async()=>
        {
            var f=new RecipeDisplayFixture();await f.Menu.Build();var oldRows=f.Menu.AllManagedItems;
            var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            f.Store.OnRead=async(_,token)=>{entered.TrySetResult();await release.Task.WaitAsync(token);};
            var build=f.Menu.Build();await entered.Task;f.View="OTHER";release.SetResult();
            try{await build;throw new Exception("Expected stale cancellation");}catch(OperationCanceledException){}
            True(ReferenceEquals(oldRows,f.Menu.AllManagedItems));
            f.Store.OnRead=null;await f.Menu.Build();True(f.Menu.SelectedRecipeFile=="OTHER.csv");AssertRecipePreview(f);
        });
        await Check("recipe_cancelled_poll_releases_guard_and_read_failure_keeps_last_valid_preview",async()=>
        {
            var f=new RecipeDisplayFixture();await f.Menu.Build();var rows=f.Menu.ShotGroupRows;
            f.Store.OnRead=(_,_)=>throw new IOException("synthetic sharing failure");
            await f.Menu.RefreshSavedReviewOffsetPreview();await f.Menu.RefreshSavedReviewOffsetPreview();
            True(ReferenceEquals(rows,f.Menu.ShotGroupRows)&&f.Messages.Count(m=>m.Contains("미리보기 갱신 실패"))==1);
            using var cancel=new CancellationTokenSource();
            f.Store.OnRead=(_,_)=>{cancel.Cancel();return Task.CompletedTask;};
            try{await f.Menu.RefreshSavedReviewOffsetPreview(cancel.Token);throw new Exception("Expected cancellation");}catch(OperationCanceledException){}
            f.Store.OnRead=null;f.Store.Put("QA",.012,-.023);await f.Menu.RefreshSavedReviewOffsetPreview();AssertRecipePreview(f);
        });
        await Check("recipe_editor_debounce_cannot_rebuild_after_view_switch",async()=>
        {
            var f=new RecipeDisplayFixture();await f.Menu.Build();var rows=f.Menu.ShotGroupRows;
            typeof(CMenuRecipe).GetMethod("SchedulePreviewRefresh",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(f.Menu,null);
            f.View="OTHER";await Task.Delay(550);True(ReferenceEquals(rows,f.Menu.ShotGroupRows));
            f.View="QA";typeof(CMenuRecipe).GetMethod("SchedulePreviewRefresh",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(f.Menu,null);
            await f.Menu.Build();rows=f.Menu.ShotGroupRows;await Task.Delay(550);True(ReferenceEquals(rows,f.Menu.ShotGroupRows));
        });
        await Check("recipe_latest_offset_poll_reads_off_calling_thread_and_rebuilds_actual_visible_shot",async()=>
        {
            var f=new RecipeDisplayFixture();await f.Menu.Build();var calling=Environment.CurrentManagedThreadId;
            var before=f.Menu.ShotGroupRows[0].CorrectedCenterX;f.Store.Put("QA",.012,-.023);
            var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            f.Store.OnRead=async(_,_)=>{entered.SetResult();await release.Task;};
            var refresh=f.Menu.RefreshSavedReviewOffsetPreview();
            try{True(entered.Task.Wait(TimeSpan.FromSeconds(5)));True(f.Store.LastReadThread!=calling);}
            finally{release.SetResult();}
            await refresh;AssertRecipePreview(f);
            True(Math.Abs(f.Menu.ShotGroupRows[0].CorrectedCenterX-before)>1e-9);
        });
    }

    private static IReadOnlyList<ST_RECIPE_PARAMETER> RecipeSaveRows(CMenuRecipe menu)
        =>(IReadOnlyList<ST_RECIPE_PARAMETER>)typeof(CMenuRecipe).GetMethod("BuildRecipeParameters",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(menu,["QA"])!;
    private static IReadOnlyDictionary<(int,int),(double,double)> RecipeFinalOffsets(CMenuRecipe menu)
        =>(IReadOnlyDictionary<(int,int),(double,double)>)typeof(CMenuRecipe).GetMethod("BuildFinalShotOffsets",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(menu,[menu.AllManagedItems,new ST_ALIGN_KEY_MARGINS(0,0)])!;
    private static void AssertRecipePreview(RecipeDisplayFixture f)
    {
        var parameters=f.Menu.AllManagedItems.ToDictionary(r=>r.Key,r=>r.Value,StringComparer.OrdinalIgnoreCase);
        parameters[CAlignKeyMarginSettings.MarginXKey]="0";parameters[CAlignKeyMarginSettings.MarginYKey]="0";
        CReviewOffsetData.ApplyToParameters(parameters,f.Store.Files.GetValueOrDefault(f.View)??ST_REVIEW_OFFSET_FILE_DATA.Empty(f.View));
        var expected=CShotCoordinatePlanBuilder.Build(parameters).Shots.GroupBy(s=>(s.CellNo,s.CellShotNo)).ToDictionary(g=>g.Key,g=>{var s=g.First();return(s.FinalScannerOffsetX-s.RecipeOffsetX,s.FinalScannerOffsetY-s.RecipeOffsetY);});
        var actual=RecipeFinalOffsets(f.Menu);True(actual.Count>0&&actual.Count==expected.Count);
        foreach(var p in expected)True(Math.Abs(actual[p.Key].Item1-p.Value.Item1)<1e-9&&Math.Abs(actual[p.Key].Item2-p.Value.Item2)<1e-9);
    }

    private sealed class RecipeDisplayFixture
    {
        public string View="QA",Selected="QA",Category="CELL";
        public readonly List<string> Messages=[];
        public readonly RecipeDisplayOffsets Store=new();
        public readonly CMenuRecipe Menu;
        public RecipeDisplayFixture()
        {
            var f=new Fixture();var recipes=new CRecipeManager(new RecipeDisplayRecipes([f.Recipe,f.Recipe with{Id="OTHER",Name="OTHER"}]));
            CMenuRecipe? menu=null;
            Menu=menu=new(recipes,f.Settings,()=>Selected,s=>Selected=s,()=>View,s=>View=s,()=>Category,s=>Category=s,()=>menu,Messages.Add,(_,_)=>{},()=>{},()=>Task.CompletedTask,Store);
            typeof(CMenuRecipe).GetField("_isCellDetailVisible",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(Menu,true);
        }
    }
    private sealed class RecipeDisplayRecipes(IReadOnlyList<ST_RECIPE_DEFINITION> recipes):CRecipeFileBase
    {
        public override Task<IReadOnlyList<ST_RECIPE_DEFINITION>> LoadAll(CancellationToken token=default)=>Task.FromResult(recipes);
        public override Task<ST_RECIPE_DEFINITION?> Find(string id,CancellationToken token=default)=>Task.FromResult(recipes.FirstOrDefault(r=>r.Id==id));
        public override Task Save(ST_RECIPE_DEFINITION recipe,CancellationToken token=default)=>throw new NotSupportedException();
        public override Task Rename(string oldId,string id,CancellationToken token=default)=>throw new NotSupportedException();
        public override Task Delete(string id,CancellationToken token=default)=>throw new NotSupportedException();
        public override Task<IReadOnlyList<ST_RECIPE_HISTORY>> LoadHistory(CancellationToken token=default)=>Task.FromResult<IReadOnlyList<ST_RECIPE_HISTORY>>([]);
        public override Task RecordSelection(string previous,string id,CancellationToken token=default)=>Task.CompletedTask;
    }
    private sealed class RecipeDisplayOffsets:CReviewOffsetFileBase
    {
        public readonly Dictionary<string,ST_REVIEW_OFFSET_FILE_DATA> Files=new(StringComparer.OrdinalIgnoreCase);
        public Func<string,CancellationToken,Task>? OnRead;
        public int Reads,LastReadThread;
        public string LastRecipe="";
        public void Put(string id,double x,double y,bool init=false)
            =>Files[id]=new(id,"synthetic/same.csv","same.csv",DateTimeOffset.UnixEpoch,[new(1,"A1",x,y){IsDesignCoordinate=true,IsMeasurementOffsetInitialized=init}]){ContentSha256=Guid.NewGuid().ToString("N")};
        public override string RootPath=>"synthetic";
        public override async Task<ST_REVIEW_OFFSET_FILE_DATA> LoadLatest(string id,CancellationToken token=default)
        {
            Interlocked.Increment(ref Reads);LastReadThread=Environment.CurrentManagedThreadId;LastRecipe=id;
            if(OnRead is not null)await OnRead(id,token);token.ThrowIfCancellationRequested();
            return Files.GetValueOrDefault(id)??ST_REVIEW_OFFSET_FILE_DATA.Empty(id);
        }
        public override Task<ST_REVIEW_OFFSET_FILE_DATA> SaveNew(string id,IReadOnlyList<ST_REVIEW_OFFSET_ROW> rows,CancellationToken token=default)=>throw new NotSupportedException();
        public override Task<ST_REVIEW_OFFSET_SOURCE_APPLICATION?> FindSourceApplication(string id,string path,CancellationToken token=default)=>throw new NotSupportedException();
        public override Task<ST_REVIEW_OFFSET_FILE_DATA> SaveNewFromSource(string id,IReadOnlyList<ST_REVIEW_OFFSET_ROW> rows,string path,bool duplicate,CancellationToken token=default)=>throw new NotSupportedException();
        public override Task<ST_REVIEW_OFFSET_ROLLBACK_RESULT> RollbackLatest(string id,string path,CancellationToken token=default)=>throw new NotSupportedException();
    }
}
