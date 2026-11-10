namespace Drilling.Common.Managers
{
    using Drilling.Common.Alarm;
    using Drilling.Common.Recipe;
    using Drilling.Common.Interface;
    using Drilling.Common.InterLock;
    using Drilling.Common.Managers;
    using Drilling.Common.Motion;
    using Drilling.Common.Station;

    public sealed class CRecipeManager(CRecipeFileBase recipeFile)
    {
        private readonly object _cacheLock = new();
        private readonly SemaphoreSlim _recipeOperationGate = new(1, 1);
        private IReadOnlyList<ST_RECIPE_DEFINITION>? _recipeCache;
        private readonly SemaphoreSlim _historyGate = new(1, 1);
        private IReadOnlyList<ST_RECIPE_HISTORY>? _historyCache;
        public Task<IReadOnlyList<ST_RECIPE_HISTORY>> LoadHistory(CancellationToken cancellationToken = default) => LoadHistoryCore(false, cancellationToken);
        private async Task<IReadOnlyList<ST_RECIPE_HISTORY>> LoadHistoryCore(bool refresh, CancellationToken cancellationToken)
        {
            await _historyGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (refresh || _historyCache is null)
                    _historyCache = (await recipeFile.LoadHistory(cancellationToken).ConfigureAwait(false)).ToArray();
                return _historyCache;
            }
            finally
            {
                _historyGate.Release();
            }
        }

        public async Task RecordSelection(string previousRecipeId, string recipeId, CancellationToken cancellationToken = default)
        {
            await recipeFile.RecordSelection(previousRecipeId, recipeId, cancellationToken);
            await LoadHistoryCore(true, cancellationToken);
        }

        public async Task<IReadOnlyList<ST_RECIPE_DEFINITION>> LoadRecipes(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_cacheLock)
            {
                if (_recipeCache is not null)
                {
                    return _recipeCache;
                }
            }

            await _recipeOperationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                lock (_cacheLock)
                {
                    if (_recipeCache is not null)
                        return _recipeCache;
                }

                var recipes = (await recipeFile.LoadAll(cancellationToken).ConfigureAwait(false)).ToArray();
                lock (_cacheLock)
                {
                    _recipeCache ??= recipes;
                    return _recipeCache;
                }
            }
            finally
            {
                _recipeOperationGate.Release();
            }
        }

        public async Task SaveRecipe(ST_RECIPE_DEFINITION recipe, CancellationToken cancellationToken = default) => await WithRecipeOperation(() => SaveRecipeCore(recipe, cancellationToken), cancellationToken).ConfigureAwait(false);
        public Task<ST_RECIPE_DEFINITION> SaveParametersIfUnchanged(ST_RECIPE_DEFINITION expected, IReadOnlyDictionary<string, string> changes, CancellationToken token = default) => WithRecipeOperation(async () =>
        {
            var current = await recipeFile.Find(expected.Id, token).ConfigureAwait(false) ?? throw new InvalidOperationException("현재 레시피가 없습니다.");
            var before = expected.Parameters.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
            var now = current.Parameters.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
            // Internal production count may change independently; never overwrite it.
            before.Remove("CURRENT_PROCESS_COUNT"); now.Remove("CURRENT_PROCESS_COUNT");
            if (before.Count != now.Count || before.Any(p => !now.TryGetValue(p.Key, out var value) || value != p.Value))
                throw new InvalidOperationException("측정 후 레시피가 변경되었습니다. 기준을 다시 설정해 주세요.");
            if (changes.Keys.Any(key => !now.ContainsKey(key))) throw new InvalidOperationException("저장할 레시피 파라미터가 없습니다.");
            var updated = current with { Parameters = current.Parameters.Select(p => changes.TryGetValue(p.Key, out var value) ? p with { Value = value } : p).ToArray() };
            // Once the file commit returns, finish readback/cache publication even if
            // the UI cancels. History must not disguise a committed Default as unsaved.
            await recipeFile.Save(updated, token).ConfigureAwait(false);
            var saved = await recipeFile.Find(expected.Id, CancellationToken.None).ConfigureAwait(false) ?? throw new IOException("레시피 저장 명령은 완료됐지만 저장값을 다시 읽을 수 없습니다.");
            UpdateCachedRecipe(saved);
            try { await LoadHistoryCore(true, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception error) { System.Diagnostics.Trace.TraceError("Recipe parameter history refresh failed after commit: " + error); }
            return saved;
        }, token);
        public Task<ST_RECIPE_DEFINITION> SaveAlignOffsetsIfUnchanged(ST_RECIPE_DEFINITION expected, IReadOnlyList<ST_ALIGN_SHOT_OFFSET> offsets, CancellationToken token = default) => WithRecipeOperation(async () =>
        {
            var current = await recipeFile.Find(expected.Id, token).ConfigureAwait(false) ?? throw new InvalidOperationException("현재 레시피가 없습니다.");
            var before = expected.Parameters.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
            var now = current.Parameters.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
            before.Remove("CURRENT_PROCESS_COUNT"); now.Remove("CURRENT_PROCESS_COUNT");
            if (before.Count != now.Count || before.Any(p => !now.TryGetValue(p.Key, out var value) || value != p.Value))
                throw new InvalidOperationException("Distortion 측정 중 레시피가 변경되었습니다. 기존 보정값을 유지합니다.");
            var updated = CAlignShotOffsetData.Apply(current, offsets);
            await recipeFile.Save(updated, token).ConfigureAwait(false);
            var saved = await recipeFile.Find(expected.Id, CancellationToken.None).ConfigureAwait(false) ?? throw new IOException("Align Offset 저장 후 레시피를 다시 읽지 못했습니다.");
            UpdateCachedRecipe(saved);
            try { await LoadHistoryCore(true, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception error) { System.Diagnostics.Trace.TraceError("Align Offset history refresh failed after commit: " + error); }
            return saved;
        }, token);

        // Merge only calibration angles into the latest Recipe under the existing operation gate.
        public Task SavePowerMeasureAngles(string recipeId, IReadOnlyDictionary<int, double> angles, CancellationToken token = default) => WithRecipeOperation(async () =>
        {
            if (angles.Count == 0 || angles.Any(pair => pair.Key is < 1 or > 8 || !double.IsFinite(pair.Value) || pair.Value is < 0 or > 360))
                throw new ArgumentException("유효한 Head별 ATT 각도가 필요합니다.");
            var recipe = await recipeFile.Find(recipeId, token).ConfigureAwait(false) ?? throw new InvalidOperationException("측정 대상 Recipe가 없습니다.");
            var values = recipe.Parameters.ToList();
            foreach (var (head, angle) in angles)
            {
                var key = $"H{head:00}_ATTENUATOR_POSITION";
                var index = values.FindIndex(p => p.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
                if (index < 0) throw new InvalidOperationException($"Recipe ATT 항목이 없습니다: {key}");
                values[index] = values[index] with { Value = angle.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture) };
            }
            await SaveRecipeCore(recipe with { Parameters = values }, token).ConfigureAwait(false);
        }, token);
        public Task<IReadOnlyList<ST_RECIPE_COMPANION_FOLDER>> ListCompanionFolders(string sourceRecipeId, string newRecipeId, CancellationToken token = default) => recipeFile is IRecipeCopyStore store ? store.ListCompanionFolders(sourceRecipeId, newRecipeId, token) : Task.FromResult<IReadOnlyList<ST_RECIPE_COMPANION_FOLDER>>([]);
        public Task CreateRecipeCopy(ST_RECIPE_DEFINITION recipe, string sourceRecipeId, IReadOnlyCollection<string> selectedFolders, CancellationToken token = default) => WithRecipeOperation(async () =>
        {
            if (recipeFile is IRecipeCopyStore store)
                await store.CreateCopy(recipe, sourceRecipeId, selectedFolders, token).ConfigureAwait(false);
            else
            {
                if (selectedFolders.Count != 0)
                    throw new InvalidOperationException("이 레시피 저장소는 부가 폴더 복사를 지원하지 않습니다.");
                if (await recipeFile.Find(recipe.Id, token).ConfigureAwait(false)is not null)
                    throw new IOException($"Recipe already exists: {recipe.Id}");
                await recipeFile.Save(recipe, token).ConfigureAwait(false);
            }

            var saved = await recipeFile.Find(recipe.Id, token).ConfigureAwait(false) ?? recipe;
            UpdateCachedRecipe(saved);
            await LoadHistoryCore(true, token).ConfigureAwait(false);
        }, token);
        private async Task SaveRecipeCore(ST_RECIPE_DEFINITION recipe, CancellationToken cancellationToken)
        {
            ST_RECIPE_DEFINITION? current;
            lock (_cacheLock)
                current = _recipeCache?.FirstOrDefault(item => item.Id.Equals(recipe.Id, StringComparison.OrdinalIgnoreCase));
            current ??= await recipeFile.Find(recipe.Id, cancellationToken);
            recipe = global::Drilling.Common.Recipe.CAlignShotOffsetData.RetainUneditedOffsets(recipe, current);
            await recipeFile.Save(recipe, cancellationToken);
            // The editor supplies History = []; retain the persisted, newly logged history instead.
            var savedRecipe = await recipeFile.Find(recipe.Id, cancellationToken) ?? recipe;
            UpdateCachedRecipe(savedRecipe);
            await LoadHistoryCore(true, cancellationToken);
        }

        public async Task<ST_RECIPE_DEFINITION?> RefreshRecipe(string recipeId, CancellationToken cancellationToken = default) => await WithRecipeOperation(() => RefreshRecipeCore(recipeId, cancellationToken), cancellationToken).ConfigureAwait(false);
        private async Task<ST_RECIPE_DEFINITION?> RefreshRecipeCore(string recipeId, CancellationToken cancellationToken)
        {
            var recipe = await recipeFile.Find(recipeId, cancellationToken);
            if (recipe is not null)
                UpdateCachedRecipe(recipe);
            else
                DeleteCachedRecipe(recipeId);
            return recipe;
        }

        public async Task RenameRecipe(string oldRecipeId, string newRecipeId, CancellationToken cancellationToken = default) => await WithRecipeOperation(() => RenameRecipeCore(oldRecipeId, newRecipeId, cancellationToken), cancellationToken).ConfigureAwait(false);
        public Task RenameRecipe(string oldRecipeId, string newRecipeId, IReadOnlyCollection<string> selectedFolders, CancellationToken token = default) => WithRecipeOperation(() => RenameRecipeCore(oldRecipeId, newRecipeId, token, selectedFolders), token);
        private async Task RenameRecipeCore(string oldRecipeId, string newRecipeId, CancellationToken cancellationToken, IReadOnlyCollection<string>? folders = null)
        {
            if (folders is not null && recipeFile is IRecipeCopyStore store)
                await store.RenameWithCompanions(oldRecipeId, newRecipeId, folders, cancellationToken);
            else
            {
                if (folders?.Count > 0)
                    throw new InvalidOperationException("이 레시피 저장소는 부가 폴더 이름 변경을 지원하지 않습니다.");
                await recipeFile.Rename(oldRecipeId, newRecipeId, cancellationToken);
            }

            RenameCachedRecipe(oldRecipeId, newRecipeId);
            var renamedRecipe = await recipeFile.Find(newRecipeId, cancellationToken);
            if (renamedRecipe is not null)
                UpdateCachedRecipe(renamedRecipe);
            await LoadHistoryCore(true, cancellationToken);
        }

        public async Task DeleteRecipe(string recipeId, CancellationToken cancellationToken = default) => await WithRecipeOperation(() => DeleteRecipeCore(recipeId, cancellationToken), cancellationToken).ConfigureAwait(false);
        public Task DeleteRecipe(string recipeId, IReadOnlyCollection<string> selectedFolders, CancellationToken token = default) => WithRecipeOperation(() => DeleteRecipeCore(recipeId, token, selectedFolders), token);
        private async Task DeleteRecipeCore(string recipeId, CancellationToken cancellationToken, IReadOnlyCollection<string>? folders = null)
        {
            if (folders is not null && recipeFile is IRecipeCopyStore store)
                await store.DeleteWithCompanions(recipeId, folders, cancellationToken);
            else
            {
                if (folders?.Count > 0)
                    throw new InvalidOperationException("이 레시피 저장소는 부가 폴더 삭제를 지원하지 않습니다.");
                await recipeFile.Delete(recipeId, cancellationToken);
            }

            DeleteCachedRecipe(recipeId);
            await LoadHistoryCore(true, cancellationToken);
        }

        public Task<int> IncrementProtectWindowCount(string recipeId, CancellationToken cancellationToken = default) => ChangeProtectWindowCount(recipeId, () => recipeFile.IncrementProtectWindowCount(recipeId, cancellationToken), cancellationToken);
        public Task<int> ResetProtectWindowCount(string recipeId, CancellationToken cancellationToken = default) => ChangeProtectWindowCount(recipeId, () => recipeFile.ResetProtectWindowCount(recipeId, cancellationToken), cancellationToken);
        public Task<int> SetProtectWindowCount(string recipeId, int count, CancellationToken cancellationToken = default) => count < 0 ? Task.FromException<int>(new ArgumentOutOfRangeException(nameof(count))) : ChangeProtectWindowCount(recipeId, () => recipeFile.SetProtectWindowCount(recipeId, count, cancellationToken), cancellationToken);
        private Task<int> ChangeProtectWindowCount(string recipeId, Func<Task<int>> commit, CancellationToken cancellationToken) => WithRecipeOperation(async () =>
        {
            // The file store commits before the new count is visible to any UI snapshot.
            var count = await commit().ConfigureAwait(false);
            lock (_cacheLock)
            {
                if (_recipeCache is not null)
                    _recipeCache = _recipeCache.Select(recipe => recipe.Id.Equals(recipeId, StringComparison.OrdinalIgnoreCase) ? recipe with { Parameters = recipe.Parameters.Select(parameter => parameter.Key.Equals(global::Drilling.Common.Recipe.CProtectWindowRecipeData.CurrentCountKey, StringComparison.OrdinalIgnoreCase) ? parameter with { Value = count.ToString(System.Globalization.CultureInfo.InvariantCulture) } : parameter).ToArray() } : recipe).ToArray();
            }

            return count;
        }, cancellationToken);
        private async Task WithRecipeOperation(Func<Task> operation, CancellationToken token)
        {
            await _recipeOperationGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                await operation().ConfigureAwait(false);
            }
            finally
            {
                _recipeOperationGate.Release();
            }
        }

        private async Task<T> WithRecipeOperation<T>(Func<Task<T>> operation, CancellationToken token)
        {
            await _recipeOperationGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                return await operation().ConfigureAwait(false);
            }
            finally
            {
                _recipeOperationGate.Release();
            }
        }

        private void UpdateCachedRecipe(ST_RECIPE_DEFINITION recipe)
        {
            lock (_cacheLock)
            {
                if (_recipeCache is null)
                {
                    return;
                }

                var recipes = new List<ST_RECIPE_DEFINITION>(_recipeCache.Count + 1);
                var updated = false;
                foreach (var cachedRecipe in _recipeCache)
                {
                    if (cachedRecipe.Id.Equals(recipe.Id, StringComparison.OrdinalIgnoreCase))
                    {
                        recipes.Add(CloneRecipe(recipe));
                        updated = true;
                        continue;
                    }

                    recipes.Add(cachedRecipe);
                }

                if (!updated)
                {
                    recipes.Add(CloneRecipe(recipe));
                }

                _recipeCache = recipes.ToArray();
            }
        }

        private void RenameCachedRecipe(string oldRecipeId, string newRecipeId)
        {
            lock (_cacheLock)
            {
                if (_recipeCache is null)
                {
                    return;
                }

                var recipes = new List<ST_RECIPE_DEFINITION>(_recipeCache.Count);
                foreach (var cachedRecipe in _recipeCache)
                {
                    if (cachedRecipe.Id.Equals(oldRecipeId, StringComparison.OrdinalIgnoreCase))
                    {
                        recipes.Add(CloneRecipe(cachedRecipe with { Id = newRecipeId, Name = newRecipeId }));
                        continue;
                    }

                    recipes.Add(cachedRecipe);
                }

                _recipeCache = recipes.ToArray();
            }
        }

        private void DeleteCachedRecipe(string recipeId)
        {
            lock (_cacheLock)
            {
                if (_recipeCache is null)
                {
                    return;
                }

                var recipes = new List<ST_RECIPE_DEFINITION>(_recipeCache.Count);
                foreach (var cachedRecipe in _recipeCache)
                {
                    if (!cachedRecipe.Id.Equals(recipeId, StringComparison.OrdinalIgnoreCase))
                    {
                        recipes.Add(cachedRecipe);
                    }
                }

                _recipeCache = recipes.ToArray();
            }
        }

        private static ST_RECIPE_DEFINITION CloneRecipe(ST_RECIPE_DEFINITION recipe)
        {
            return recipe with
            {
                Parameters = recipe.Parameters.ToArray(),
                History = recipe.History.ToArray()
            };
        }
    }

    public sealed record ST_RECIPE_COMPANION_FOLDER(string SourceName, string TargetName);
    /// <summary>Optional file-store capability for recipe-owned companion folders.</summary>
    public interface IRecipeCopyStore
    {
        Task<IReadOnlyList<ST_RECIPE_COMPANION_FOLDER>> ListCompanionFolders(string sourceRecipeId, string newRecipeId, CancellationToken token = default);
        Task CreateCopy(ST_RECIPE_DEFINITION recipe, string sourceRecipeId, IReadOnlyCollection<string> selectedFolders, CancellationToken token = default);
        Task RenameWithCompanions(string oldRecipeId, string newRecipeId, IReadOnlyCollection<string> selectedFolders, CancellationToken token = default);
        Task DeleteWithCompanions(string recipeId, IReadOnlyCollection<string> selectedFolders, CancellationToken token = default);
    }

    public enum EN_RECIPE_DATA_TYPE
    {
        String,
        Int,
        Double,
        Bool
    }

    public sealed record ST_RECIPE_PARAMETER(
        string Name, 
        string Value, 
        string Unit, 
        string Range, 
        string DefaultValue, 
        string Tab = "", 
        string Group = "", 
        string Key = "", 
        string Description = "", 
        bool Show = true, 
        bool Use = true, 
        int DisplayOrder = 0, 
        EN_RECIPE_DATA_TYPE DataType = EN_RECIPE_DATA_TYPE.String, 
        double ChangeLimit = 0.0, 
        double Min = 0.0, 
        double Max = 0.0, 
        IReadOnlyDictionary<string, string>? Extra = null
    );
    public sealed record ST_RECIPE_HISTORY(
        DateTimeOffset ChangedAt, 
        string ItemName, 
        string OldValue, 
        string NewValue, 
        string OperatorId, 
        string RecipeName = "", 
        string Action = "", 
        string Tab = "", 
        string Group = ""
    );
    public sealed record ST_RECIPE_DEFINITION(string Id, string Name, IReadOnlyList<ST_RECIPE_PARAMETER> Parameters, IReadOnlyList<ST_RECIPE_HISTORY> History);
    public sealed record ST_RECIPE_FORM_ITEM(
        string Tab, 
        string Group, 
        string Name, 
        string DisplayName, 
        string CimName, 
        EN_RECIPE_DATA_TYPE DataType, 
        string Unit, 
        bool Show, 
        bool Use, 
        string DefaultValue, 
        double Scale, 
        double ChangeLimit, 
        double Min, 
        double Max, 
        string Description, 
        int DisplayOrder, 
        IReadOnlyDictionary<string, string>? Extra = null
    );
    public sealed record ST_RECIPE_VALUE(string Tab, string Name, string Value, IReadOnlyList<string>? Extra = null);
    public abstract class CRecipeFileBase
    {
        public abstract Task<IReadOnlyList<ST_RECIPE_DEFINITION>> LoadAll(CancellationToken cancellationToken = default);
        public abstract Task<ST_RECIPE_DEFINITION?> Find(string recipeId, CancellationToken cancellationToken = default);
        public abstract Task Save(ST_RECIPE_DEFINITION recipe, CancellationToken cancellationToken = default);
        public abstract Task Rename(string oldRecipeId, string newRecipeId, CancellationToken cancellationToken = default);
        public abstract Task Delete(string recipeId, CancellationToken cancellationToken = default);
        public abstract Task<IReadOnlyList<ST_RECIPE_HISTORY>> LoadHistory(CancellationToken cancellationToken = default);
        public abstract Task RecordSelection(string previousRecipeId, string recipeId, CancellationToken cancellationToken = default);
        public virtual Task<int> IncrementProtectWindowCount(string recipeId, CancellationToken cancellationToken = default) => throw new NotSupportedException("This recipe store does not support persistent Protect Window counts.");
        public virtual Task<int> ResetProtectWindowCount(string recipeId, CancellationToken cancellationToken = default) => throw new NotSupportedException("This recipe store does not support resetting Protect Window counts.");
        public virtual Task<int> SetProtectWindowCount(string recipeId, int count, CancellationToken cancellationToken = default) => throw new NotSupportedException("This recipe store does not support Protect Window count handover.");
    }
}

namespace Drilling.Common.Recipe
{
    using System.Globalization;

    public static class CProtectWindowRecipeData
    {
        public const string Tab = "PROCESS";
        public const string Group = "ProtectWindow";
        public const string MaximumCountKey = "MAX_PROCESS_COUNT";
        public const string CurrentCountKey = "CURRENT_PROCESS_COUNT";
        public static int ParseCount(string? text, string key = CurrentCountKey)
        {
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var count) || !double.IsFinite(count) || count < 0 || count > int.MaxValue || count != Math.Truncate(count))
                throw new InvalidDataException($"{key} must be a nonnegative integer: {text}");
            return (int)count;
        }
    }
}


