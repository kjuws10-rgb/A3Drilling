using Drilling.Common.Review;
using Drilling.UI.Menu.Menus;

internal static partial class Program
{
    // Calls only the production pure matcher. Native stationary feedback/device guards are
    // source-reviewed, never executed by this generic no-equipment harness.
    private static async Task RunCurrentPositionMatchChecks()
    {
        static ST_REVIEW_PLAN_POINT Target(double x,double y,bool use=true)=>new(1,"S1",1,1,1,1,1,use,100,200,x,y,0,0,EN_REVIEW_POINT_STATE.Ng,"NG");
        static IReadOnlyList<string> Match(IReadOnlyDictionary<string,ST_REVIEW_PLAN_POINT> targets,double x,double y)
            =>(IReadOnlyList<string>)typeof(CManualInspectionBackend).GetMethod("FindPositionTargets",System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic)!.Invoke(null,[targets,x,y])!;
        await Check("native_position_match_uses_actual_review_targets_not_design_or_recent_error",()=>
        {
            var targets=new Dictionary<string,ST_REVIEW_PLAN_POINT>{{"P1",Target(1,2)}};
            True(Match(targets,1,2).SequenceEqual(["P1"]));
            True(Match(targets,100,200).Count==0);
            True(Match(targets,0,0).Count==0);return Task.CompletedTask;
        });
        await Check("native_position_match_requires_both_axes_within_half_micrometre_and_never_falls_back_to_nearest",()=>
        {
            var targets=new Dictionary<string,ST_REVIEW_PLAN_POINT>{{"P1",Target(0,0)}};
            True(Match(targets,.0005,-.0005).SequenceEqual(["P1"]));
            True(Match(targets,.0005001,0).Count==0);
            True(Match(targets,0,-.0005001).Count==0);
            True(Match(targets,100,200).Count==0);return Task.CompletedTask;
        });
        await Check("native_position_match_excludes_disabled_targets_and_nonfinite_feedback_without_inventing_a_point",()=>
        {
            var targets=new Dictionary<string,ST_REVIEW_PLAN_POINT>{{"DISABLED",Target(1,2,false)},{"NAN",Target(double.NaN,2)}};
            True(Match(targets,1,2).Count==0);
            foreach(var value in new[]{double.NaN,double.PositiveInfinity,double.NegativeInfinity})
            {True(Match(targets,value,2).Count==0);True(Match(targets,1,value).Count==0);}
            return Task.CompletedTask;
        });
        await Check("native_position_match_preserves_all_ambiguous_matches_for_native_rejection_instead_of_picking_first",()=>
        {
            var targets=new Dictionary<string,ST_REVIEW_PLAN_POINT>{{"CELL1",Target(1,2)},{"CELL2",Target(1.0001,2) with{CellNo=2,ShotKey="S2"}}};
            var matches=Match(targets,1,2);
            True(matches.Count==2&&matches.ToHashSet().SetEquals(["CELL1","CELL2"]));return Task.CompletedTask;
        });
    }
}
