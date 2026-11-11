namespace Drilling.File.JHMI
{
    using System.Diagnostics;
    using Drilling.Common.Managers;
    using Drilling.Common.Log;
    using System.Globalization;
    using Drilling.Common.Interface;
    using Drilling.Common.Motion;
    using Drilling.Common.Alarm;
    using Drilling.Common.InterLock;
    using Drilling.Common.Station;
    using Drilling.Common.Recipe;
    using Drilling.File.Parser;
    using System.Text;

    public sealed partial class CJhmiRecipeFile(string configRoot) : CRecipeFileBase, IRecipeCopyStore
    {
#region 공유 상태·초기값
        private static readonly HashSet<string> CellScopedTemplateNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "CELL_ALIGN_TO_1ST_PIXEL_X",
            "CELL_ALIGN_TO_1ST_PIXEL_Y",
            "CELL_ROTATION",
            "CELL_MODEL_TYPE"
        };
        private static readonly IReadOnlyList<string> FormHeaders = ["TAB", "GROUP", "NAME", "DISPLAY NAME", "CIM NAME", "DATA TYPE", "UNIT", "SHOW", "USE", "VALUE", "SCALE", "CHANGE LIMIT", "MIN", "MAX", "DESCRIPTION", "ORDER"];
        private readonly string _configRoot = configRoot;
        private readonly string _recipeDirectory = Path.Combine(configRoot, "RECIPE");
        private readonly CLogManager _logManager = new(configRoot);
        private readonly SemaphoreSlim _fileOperationLock = new(1, 1);
#endregion
#region 기본 처리
        public override Task<IReadOnlyList<ST_RECIPE_HISTORY>> LoadHistory(CancellationToken cancellationToken = default) => ExecuteFileOperation(() => _logManager.ReadRecipeRecent(), cancellationToken);
        public override Task RecordSelection(string previousRecipeId, string recipeId, CancellationToken cancellationToken = default)
        {
            return ExecuteFileOperation(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!System.IO.File.Exists(GetRecipePath(recipeId)))
                    throw new FileNotFoundException($"Recipe {recipeId} was not found.");
                string RecipeName(string id)
                {
                    if (string.IsNullOrWhiteSpace(id) || !System.IO.File.Exists(GetRecipePath(id)))
                        return id;
                    return ReadRecipeValues(GetRecipePath(id), cancellationToken).LastOrDefault(value => value.Tab.Equals("DEFAULT", StringComparison.OrdinalIgnoreCase) && value.Name.Equals("RECIPE_NAME", StringComparison.OrdinalIgnoreCase))?.Value ?? id;
                }

                _logManager.WriteRecipeSelect(RecipeName(previousRecipeId), RecipeName(recipeId));
            }, cancellationToken);
        }

        public override Task<IReadOnlyList<ST_RECIPE_DEFINITION>> LoadAll(CancellationToken cancellationToken = default)
        {
            global::System.Collections.Generic.IReadOnlyList<global::Drilling.Common.Managers.ST_RECIPE_DEFINITION> SelectValue()
            {
                return LoadAllCore(cancellationToken);
            }

            return ExecuteFileOperation(SelectValue, cancellationToken);
        }

        private IReadOnlyList<ST_RECIPE_DEFINITION> LoadAllCore(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(_recipeDirectory))
            {
                return[];
            }

            var formItems = LoadFormItems();
            cancellationToken.ThrowIfCancellationRequested();
            var recipes = new List<ST_RECIPE_DEFINITION>();
            foreach (var path in Directory.EnumerateFiles(_recipeDirectory, "*.csv"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var recipe = LoadRecipe(path, formItems, cancellationToken);
                if (recipe is not null)
                {
                    recipes.Add(recipe);
                }
            }

            string SelectName(global::Drilling.Common.Managers.ST_RECIPE_DEFINITION recipe)
            {
                return recipe.Name;
            }

            return recipes.OrderBy(SelectName).ToArray();
        }

        public override Task<ST_RECIPE_DEFINITION?> Find(string recipeId, CancellationToken cancellationToken = default)
        {
            global::Drilling.Common.Managers.ST_RECIPE_DEFINITION? SelectValue()
            {
                return FindCore(recipeId, cancellationToken);
            }

            return ExecuteFileOperation(SelectValue, cancellationToken);
        }

        private ST_RECIPE_DEFINITION? FindCore(string recipeId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var formItems = LoadFormItems();
            cancellationToken.ThrowIfCancellationRequested();
            var path = GetRecipePath(recipeId);
            return System.IO.File.Exists(path) ? LoadRecipe(path, formItems, cancellationToken) : null;
        }

        public override Task Save(ST_RECIPE_DEFINITION recipe, CancellationToken cancellationToken = default)
        {
            void SaveCoreCallback()
            {
                SaveCore(recipe, cancellationToken);
            }

            return ExecuteFileOperation(SaveCoreCallback, cancellationToken);
        }

        private void SaveCore(ST_RECIPE_DEFINITION recipe, CancellationToken cancellationToken, string? destination = null, bool writeHistory = true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(_recipeDirectory);
            var formItems = LoadFormItems();
            var recipePath = destination ?? GetRecipePath(recipe.Id);
            var recipeExists = System.IO.File.Exists(recipePath);
            string SelectGroupKey(global::Drilling.Common.Managers.ST_RECIPE_VALUE item)
            {
                return CreateKey(item.Tab, item.Name);
            }

            string SelectKey(global::System.Linq.IGrouping<string, global::Drilling.Common.Managers.ST_RECIPE_VALUE> group)
            {
                return group.Key;
            }

            string SelectValue(global::System.Linq.IGrouping<string, global::Drilling.Common.Managers.ST_RECIPE_VALUE> group)
            {
                return group.Last().Value;
            }

            var oldValues = recipeExists ? ReadRecipeValues(recipePath, cancellationToken).GroupBy(SelectGroupKey, StringComparer.OrdinalIgnoreCase).ToDictionary(SelectKey, SelectValue, StringComparer.OrdinalIgnoreCase) : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            bool ShouldIncludeItem(global::Drilling.Common.Managers.ST_RECIPE_PARAMETER parameter)
            {
                return !IsCellScopedKey(parameter.Key);
            }

            var valuesByKey = recipe.Parameters.Where(ShouldIncludeItem).Select(parameter => new { Parameter = parameter, FormItem = FindFormItem(formItems, parameter) }).Where(item => item.FormItem is not null).ToDictionary(item => CreateKey(item.FormItem!.Tab, item.FormItem.Name), item => item.Parameter.Value, StringComparer.OrdinalIgnoreCase);
            var activeCellCount = GetActiveCellCount(recipe.Parameters);
            var shotGroupBounds = CalculateActiveShotGroupBoundsByCell(recipe.Parameters, activeCellCount);
            bool ShouldIncludeRecipe(global::Drilling.Common.Managers.ST_RECIPE_PARAMETER parameter)
            {
                return IsCellScopedKey(parameter.Key) && GetCellNo(parameter.Key) <= activeCellCount && IsShotGroupOverrideWithinActivePattern(parameter, shotGroupBounds);
            }

            int SelectSortKey(global::Drilling.Common.Managers.ST_RECIPE_PARAMETER parameter)
            {
                return GetCellNo(parameter.Key);
            }

            string SelectRecipeParameterKey(global::Drilling.Common.Managers.ST_RECIPE_PARAMETER parameter)
            {
                return parameter.Key;
            }

            var cellScopedParameters = recipe.Parameters.Where(ShouldIncludeRecipe).OrderBy(SelectSortKey).ThenBy(SelectRecipeParameterKey, StringComparer.OrdinalIgnoreCase).ToArray();
            var lines = new List<string>
            {
                FormatRecipeLine("DEFAULT", "RECIPE_NAME", recipe.Name),
                FormatRecipeLine("DEFAULT", "RECIPE_ID", recipe.Id)
            };
            var expectedValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [CreateKey("DEFAULT", "RECIPE_NAME")] = recipe.Name,
                [CreateKey("DEFAULT", "RECIPE_ID")] = recipe.Id
            };
            var changedItems = new List<(string Tab, string Group, string ItemName, string OldValue, string NewValue)>();
            bool ShouldIncludeRecipe2(global::Drilling.Common.Managers.ST_RECIPE_FORM_ITEM item)
            {
                return item.Use && !IsCellScopedTemplate(item);
            }

            int SelectDisplayOrder(global::Drilling.Common.Managers.ST_RECIPE_FORM_ITEM item)
            {
                return item.DisplayOrder;
            }

            foreach (var item in formItems.Where(ShouldIncludeRecipe2).OrderBy(SelectDisplayOrder))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var key = CreateKey(item.Tab, item.Name);
                var value = valuesByKey.TryGetValue(key, out var editedValue) ? editedValue : item.DefaultValue;
                // Runtime-owned count must survive an editor/offset draft opened before machining completed.
                if (item.Tab.Equals(CProtectWindowRecipeData.Tab, StringComparison.OrdinalIgnoreCase) && item.Name.Equals(CProtectWindowRecipeData.CurrentCountKey, StringComparison.OrdinalIgnoreCase))
                    value = recipeExists ? GetValue(oldValues, item.Tab, item.Name, item.DefaultValue) : item.DefaultValue;
                if (recipeExists)
                {
                    var oldValue = GetValue(oldValues, item.Tab, item.Name, item.DefaultValue);
                    if (!oldValue.Equals(value, StringComparison.Ordinal))
                    {
                        changedItems.Add((item.Tab, item.Group, item.DisplayName, oldValue, value));
                    }
                }

                expectedValues[CreateKey(item.Tab, item.Name)] = value;
                lines.Add(FormatRecipeLine(item.Tab, item.Name, value));
            }

            foreach (var parameter in cellScopedParameters)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var persistedKey = ToPersistedCellScopedKey(parameter.Key);
                if (recipeExists)
                {
                    var oldValue = GetValue(oldValues, "CELL", persistedKey, parameter.DefaultValue);
                    if (!oldValue.Equals(parameter.Value, StringComparison.Ordinal))
                    {
                        changedItems.Add(("CELL", parameter.Group, GetCellHistoryItemName(parameter, recipe.Parameters), oldValue, parameter.Value));
                    }
                }

                expectedValues[CreateKey("CELL", persistedKey)] = parameter.Value;
                lines.Add(FormatRecipeLine("CELL", persistedKey, parameter.Value));
            }

            cancellationToken.ThrowIfCancellationRequested();
            CCsvParser.WriteLines(recipePath, lines);
            ValidateSavedRecipeFile(recipePath, expectedValues, cancellationToken);
            if (!writeHistory)
                return;
            var recipeName = GetRecipeName(recipe);
            if (recipeExists)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _logManager.WriteRecipeModifyBatch(recipeName, changedItems);
                _logManager.WriteRecipeSave(recipeName);
            }
            else
            {
                _logManager.WriteRecipeCreate(recipeName);
            }
        }

        public override Task Rename(string oldRecipeId, string newRecipeId, CancellationToken cancellationToken = default)
        {
            void RenameCoreCallback()
            {
                RenameCore(oldRecipeId, newRecipeId, cancellationToken);
            }

            return ExecuteFileOperation(RenameCoreCallback, cancellationToken);
        }

        private string? RenameCore(string oldRecipeId, string newRecipeId, CancellationToken cancellationToken, bool writeHistory = true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(_recipeDirectory);
            var oldPath = GetRecipePath(oldRecipeId);
            var newPath = GetRecipePath(newRecipeId);
            if (!System.IO.File.Exists(oldPath))
            {
                return null;
            }

            if (System.IO.File.Exists(newPath))
            {
                throw new IOException($"Recipe file already exists. {newRecipeId}.csv");
            }

            var formItems = LoadFormItems();
            var recipeValues = ReadRecipeValues(oldPath, cancellationToken);
            string SelectGroupKey(global::Drilling.Common.Managers.ST_RECIPE_VALUE item)
            {
                return CreateKey(item.Tab, item.Name);
            }

            string SelectKey(global::System.Linq.IGrouping<string, global::Drilling.Common.Managers.ST_RECIPE_VALUE> group)
            {
                return group.Key;
            }

            string SelectValue(global::System.Linq.IGrouping<string, global::Drilling.Common.Managers.ST_RECIPE_VALUE> group)
            {
                return group.Last().Value;
            }

            var oldValues = recipeValues.GroupBy(SelectGroupKey, StringComparer.OrdinalIgnoreCase).ToDictionary(SelectKey, SelectValue, StringComparer.OrdinalIgnoreCase);
            var oldRecipeName = GetValue(oldValues, "DEFAULT", "RECIPE_NAME", oldRecipeId);
            var activeCellCount = GetActiveCellCount(oldValues, formItems);
            var expectedValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [CreateKey("DEFAULT", "RECIPE_NAME")] = newRecipeId,
                [CreateKey("DEFAULT", "RECIPE_ID")] = newRecipeId
            };
            var lines = new List<string>
            {
                FormatRecipeLine("DEFAULT", "RECIPE_NAME", newRecipeId),
                FormatRecipeLine("DEFAULT", "RECIPE_ID", newRecipeId)
            };
            bool ShouldIncludeItem(global::Drilling.Common.Managers.ST_RECIPE_FORM_ITEM item)
            {
                return item.Use && !IsCellScopedTemplate(item);
            }

            int SelectDisplayOrder(global::Drilling.Common.Managers.ST_RECIPE_FORM_ITEM item)
            {
                return item.DisplayOrder;
            }

            foreach (var item in formItems.Where(ShouldIncludeItem).OrderBy(SelectDisplayOrder))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var value = item.Name.Equals("RECIPE_NAME", StringComparison.OrdinalIgnoreCase) ? newRecipeId : GetValue(oldValues, item.Tab, item.Name, item.DefaultValue);
                expectedValues[CreateKey(item.Tab, item.Name)] = value;
                lines.Add(FormatRecipeLine(item.Tab, item.Name, value));
            }

            bool MatchesTab(global::Drilling.Common.Managers.ST_RECIPE_VALUE item)
            {
                return item.Tab.Equals("CELL", StringComparison.OrdinalIgnoreCase) && IsCellScopedKey(item.Name) && GetCellNo(item.Name) <= activeCellCount;
            }

            int SelectSortKey(global::Drilling.Common.Managers.ST_RECIPE_VALUE item)
            {
                return GetCellNo(item.Name);
            }

            string SelectName(global::Drilling.Common.Managers.ST_RECIPE_VALUE item)
            {
                return item.Name;
            }

            foreach (var cellValue in recipeValues.Where(MatchesTab).OrderBy(SelectSortKey).ThenBy(SelectName, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var parameterName = NormalizeCellScopedParameterName(GetCellParameterName(cellValue.Name));
                var scopedKey = $"CELL{GetCellNo(cellValue.Name)}_{parameterName}";
                var persistedKey = ToPersistedCellScopedKey(scopedKey);
                expectedValues[CreateKey("CELL", persistedKey)] = cellValue.Value;
                lines.Add(FormatRecipeLine("CELL", persistedKey, cellValue.Value));
            }

            var temporary = newPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                CCsvParser.WriteLines(temporary, lines);
                ValidateSavedRecipeFile(temporary, expectedValues, cancellationToken);
                System.IO.File.Move(temporary, newPath, overwrite: false);
                try
                {
                    System.IO.File.Delete(oldPath);
                }
                catch
                {
                    System.IO.File.Delete(newPath);
                    throw;
                }
            }
            finally
            {
                if (System.IO.File.Exists(temporary))
                    System.IO.File.Delete(temporary);
            }

            if (writeHistory)
                _logManager.WriteRecipeRename(oldRecipeName, newRecipeId);
            return oldRecipeName;
        }

        public override Task Delete(string recipeId, CancellationToken cancellationToken = default)
        {
            void DeleteCoreCallback()
            {
                DeleteCore(recipeId, cancellationToken);
            }

            return ExecuteFileOperation(DeleteCoreCallback, cancellationToken);
        }

        private string? DeleteCore(string recipeId, CancellationToken cancellationToken, bool writeHistory = true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = GetRecipePath(recipeId);
            var recipeName = recipeId;
            var recipeExists = System.IO.File.Exists(path);
            string SelectGroupKey(global::Drilling.Common.Managers.ST_RECIPE_VALUE item)
            {
                return CreateKey(item.Tab, item.Name);
            }

            string SelectKey(global::System.Linq.IGrouping<string, global::Drilling.Common.Managers.ST_RECIPE_VALUE> group)
            {
                return group.Key;
            }

            string SelectValue(global::System.Linq.IGrouping<string, global::Drilling.Common.Managers.ST_RECIPE_VALUE> group)
            {
                return group.Last().Value;
            }

            if (recipeExists)
            {
                var values = ReadRecipeValues(path, cancellationToken).GroupBy(SelectGroupKey, StringComparer.OrdinalIgnoreCase).ToDictionary(SelectKey, SelectValue, StringComparer.OrdinalIgnoreCase);
                recipeName = GetValue(values, "DEFAULT", "RECIPE_NAME", recipeId);
            }

            DeleteIfExists(path);
            if (recipeExists && writeHistory)
            {
                _logManager.WriteRecipeDelete(recipeName);
            }

            return recipeExists ? recipeName : null;
        }

        private async Task<T> ExecuteFileOperation<T>(Func<T> operation, CancellationToken cancellationToken)
        {
            await _fileOperationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await Task.Run(operation, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _fileOperationLock.Release();
            }
        }

        private async Task ExecuteFileOperation(Action operation, CancellationToken cancellationToken)
        {
            await _fileOperationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await Task.Run(operation, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _fileOperationLock.Release();
            }
        }

        private ST_RECIPE_DEFINITION? LoadRecipe(string path, IReadOnlyList<ST_RECIPE_FORM_ITEM> formItems, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var recipeId = Path.GetFileNameWithoutExtension(path);
            if (string.IsNullOrWhiteSpace(recipeId))
            {
                return null;
            }

            var recipeValues = ReadRecipeValues(path, cancellationToken);
            string SelectGroupKey(global::Drilling.Common.Managers.ST_RECIPE_VALUE item)
            {
                return CreateKey(item.Tab, item.Name);
            }

            string SelectKey(global::System.Linq.IGrouping<string, global::Drilling.Common.Managers.ST_RECIPE_VALUE> group)
            {
                return group.Key;
            }

            string SelectValue(global::System.Linq.IGrouping<string, global::Drilling.Common.Managers.ST_RECIPE_VALUE> group)
            {
                return group.Last().Value;
            }

            var values = recipeValues.GroupBy(SelectGroupKey, StringComparer.OrdinalIgnoreCase).ToDictionary(SelectKey, SelectValue, StringComparer.OrdinalIgnoreCase);
            cancellationToken.ThrowIfCancellationRequested();
            var recipeName = GetValue(values, "DEFAULT", "RECIPE_NAME", recipeId);
            bool IsUse(global::Drilling.Common.Managers.ST_RECIPE_FORM_ITEM item)
            {
                return item.Use;
            }

            int SelectDisplayOrder(global::Drilling.Common.Managers.ST_RECIPE_FORM_ITEM item)
            {
                return item.DisplayOrder;
            }

            global::Drilling.Common.Managers.ST_RECIPE_PARAMETER BuildRecipe(global::Drilling.Common.Managers.ST_RECIPE_FORM_ITEM item)
            {
                return new ST_RECIPE_PARAMETER(
                    item.DisplayName, 
                    GetValue(values, item.Tab, item.Name, item.DefaultValue), 
                    item.Unit, 
                    $"{item.Min.ToString("0.###", CultureInfo.InvariantCulture)} - {item.Max.ToString("0.###", CultureInfo.InvariantCulture)}", 
                    item.DefaultValue, 
                    item.Tab, 
                    item.Group, 
                    item.Name, 
                    item.Description, 
                    item.Show, 
                    item.Use, 
                    item.DisplayOrder, 
                    item.DataType, 
                    item.ChangeLimit, 
                    item.Min, 
                    item.Max, 
                    item.Extra
                );
            }

            var parameters = formItems.Where(IsUse).OrderBy(SelectDisplayOrder).Select(BuildRecipe).ToList();
            string SelectName(global::Drilling.Common.Managers.ST_RECIPE_FORM_ITEM item)
            {
                return item.Name;
            }

            string SelectFormItemGroupKey(global::System.Linq.IGrouping<string, global::Drilling.Common.Managers.ST_RECIPE_FORM_ITEM> group)
            {
                return group.Key;
            }

            global::Drilling.Common.Managers.ST_RECIPE_FORM_ITEM SelectRecipe(global::System.Linq.IGrouping<string, global::Drilling.Common.Managers.ST_RECIPE_FORM_ITEM> group)
            {
                return group.First();
            }

            var formItemsByName = formItems.GroupBy(SelectName, StringComparer.OrdinalIgnoreCase).ToDictionary(SelectFormItemGroupKey, SelectRecipe, StringComparer.OrdinalIgnoreCase);
            string SelectParameterKey(global::Drilling.Common.Managers.ST_RECIPE_PARAMETER parameter)
            {
                return parameter.Key;
            }

            var parameterKeys = new HashSet<string>(parameters.Select(SelectParameterKey), StringComparer.OrdinalIgnoreCase);
            bool ShouldIncludeItem(global::Drilling.Common.Managers.ST_RECIPE_VALUE item)
            {
                return item.Tab.Equals("CELL", StringComparison.OrdinalIgnoreCase) && IsCellScopedKey(item.Name);
            }

            int SelectSortKey(global::Drilling.Common.Managers.ST_RECIPE_VALUE item)
            {
                return GetCellNo(item.Name);
            }

            string SelectName2(global::Drilling.Common.Managers.ST_RECIPE_VALUE item)
            {
                return item.Name;
            }

            foreach (var cellValue in recipeValues.Where(ShouldIncludeItem).OrderBy(SelectSortKey).ThenBy(SelectName2, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var parameterName = NormalizeCellScopedParameterName(GetCellParameterName(cellValue.Name));
                ST_RECIPE_FORM_ITEM? template = null;
                var hasTemplate = IsCellPlacementParameterName(parameterName) && (formItemsByName.TryGetValue(parameterName, out template) || formItemsByName.TryGetValue($"CELL_{parameterName}", out template));
                var normalizedScopedKey = $"CELL{GetCellNo(cellValue.Name)}_{parameterName}";
                if (parameterKeys.Contains(normalizedScopedKey))
                {
                    continue;
                }

                if (!hasTemplate)
                {
                    var shotGroupOverride = CreateShotGroupOverrideParameter(GetCellNo(cellValue.Name), parameterName, normalizedScopedKey, cellValue.Value);
                    if (shotGroupOverride is not null)
                    {
                        parameters.Add(shotGroupOverride);
                        parameterKeys.Add(normalizedScopedKey);
                    }

                    continue;
                }

                parameters.Add(new ST_RECIPE_PARAMETER(
                    template!.DisplayName, 
                    cellValue.Value, 
                    template.Unit, 
                    $"{template.Min.ToString("0.###", CultureInfo.InvariantCulture)} - {template.Max.ToString("0.###", CultureInfo.InvariantCulture)}", 
                    template.DefaultValue, 
                    "CELL", 
                    GetCellScopedGroup(parameterName), 
                    normalizedScopedKey, 
                    template.Description, 
                    true, 
                    true, 
                    template.DisplayOrder, 
                    template.DataType, 
                    template.ChangeLimit, 
                    template.Min, 
                    template.Max, 
                    template.Extra
                ));
                parameterKeys.Add(normalizedScopedKey);
            }

            var history = _logManager.ReadRecipeRecent(recipeName, recipeId);
            return new ST_RECIPE_DEFINITION(recipeId, recipeName, parameters.ToArray(), history);
        }

        private static bool IsCellScopedKey(string key)
        {
            return GetCellNo(key) > 0 && !string.IsNullOrWhiteSpace(GetCellParameterName(key));
        }

        private static bool IsCellScopedTemplate(ST_RECIPE_FORM_ITEM item)
        {
            return item.Tab.Equals("PROCESS", StringComparison.OrdinalIgnoreCase) && CellScopedTemplateNames.Contains(item.Name);
        }

        private static int GetActiveCellCount(IEnumerable<ST_RECIPE_PARAMETER> parameters)
        {
            bool MatchesItem(global::Drilling.Common.Managers.ST_RECIPE_PARAMETER parameter)
            {
                return parameter.Key.Equals("MAX_CELL_NUMBER", StringComparison.OrdinalIgnoreCase);
            }

            var countValue = parameters.FirstOrDefault(MatchesItem)?.Value;
            var countItem = parameters.FirstOrDefault(MatchesItem);
            var minimum = GetActiveCellCountMinimum(countItem?.Min, countItem?.Max);
            var maximum = GetActiveCellCountMaximum(countItem?.Max, minimum);
            return ParseActiveCellCount(countValue, minimum, maximum);
        }

        private static int ReadPositiveInt(IEnumerable<ST_RECIPE_PARAMETER> parameters, string key)
        {
            bool MatchesItem(global::Drilling.Common.Managers.ST_RECIPE_PARAMETER parameter)
            {
                return parameter.Key.Equals(key, StringComparison.OrdinalIgnoreCase);
            }

            var value = parameters.FirstOrDefault(MatchesItem)?.Value;
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0 ? parsed : 0;
        }

        private static IReadOnlyDictionary<int, ST_SHOT_GROUP_PATTERN_BOUNDS> CalculateActiveShotGroupBoundsByCell(IReadOnlyList<ST_RECIPE_PARAMETER> parameters, int activeCellCount)
        {
            var modelTypeCount = Math.Clamp(ReadPositiveInt(parameters, "CELL_MODEL_TYPE_COUNT"), 1, CRecipeModelPixelCountReader.MaximumCellModelTypeCount);
            var processPitch = ReadPositiveInt(parameters, "PITCH");
            var splitBeamCount = ReadPositiveInt(parameters, "SPLITED_BEAM_COUNT");
            var boundsByModel = new Dictionary<int, ST_SHOT_GROUP_PATTERN_BOUNDS>(modelTypeCount);
            for (var modelType = 1; modelType <= modelTypeCount; modelType++)
            {
                var pixelCountX = ReadPositiveInt(parameters, $"MODEL{modelType}_NUM_OF_PIXEL_X");
                var pixelCountY = ReadPositiveInt(parameters, $"MODEL{modelType}_NUM_OF_PIXEL_Y");
                boundsByModel[modelType] = pixelCountX <= 0 || pixelCountY <= 0 || processPitch <= 0 || splitBeamCount <= 0 ? new ST_SHOT_GROUP_PATTERN_BOUNDS(false, 0, 0) : new ST_SHOT_GROUP_PATTERN_BOUNDS(true, pixelCountX / processPitch, pixelCountY / processPitch);
            }

            var boundsByCell = new Dictionary<int, ST_SHOT_GROUP_PATTERN_BOUNDS>(activeCellCount);
            for (var cellNo = 1; cellNo <= activeCellCount; cellNo++)
            {
                var modelType = Math.Clamp(ReadPositiveInt(parameters, $"CELL{cellNo}_MODEL_TYPE"), 1, modelTypeCount);
                boundsByCell[cellNo] = boundsByModel.TryGetValue(modelType, out var bounds) ? bounds : new ST_SHOT_GROUP_PATTERN_BOUNDS(false, 0, 0);
            }

            return boundsByCell;
        }

        private static bool IsShotGroupOverrideWithinActivePattern(ST_RECIPE_PARAMETER parameter, IReadOnlyDictionary<int, ST_SHOT_GROUP_PATTERN_BOUNDS> boundsByCell)
        {
            if (!boundsByCell.TryGetValue(GetCellNo(parameter.Key), out var bounds))
            {
                return true;
            }

            if (!bounds.IsDefined)
            {
                return true;
            }

            var parameterName = NormalizeCellScopedParameterName(GetCellParameterName(parameter.Key));
            return !TryGetShotGroupOverrideMetadata(parameterName, out _, out var column, out var row, out _, out _, out _, out _) || column <= bounds.GroupCountX && row <= bounds.GroupCountY;
        }

        private static int GetActiveCellCount(IReadOnlyDictionary<string, string> values, IReadOnlyList<ST_RECIPE_FORM_ITEM> formItems)
        {
            var countValue = GetValue(values, "COMMON", "MAX_CELL_NUMBER", "");
            var countItem = formItems.FirstOrDefault(item => item.Name.Equals("MAX_CELL_NUMBER", StringComparison.OrdinalIgnoreCase));
            var minimum = GetActiveCellCountMinimum(countItem?.Min, countItem?.Max);
            var maximum = GetActiveCellCountMaximum(countItem?.Max, minimum);
            return ParseActiveCellCount(countValue, minimum, maximum);
        }

        private static int ParseActiveCellCount(string? value, int minimum, int? maximum)
        {
            if (!CCellPatternCalculator.TryParseCellCount(value, minimum, maximum, out var count, out var validationMessage))
            {
                throw new InvalidDataException(validationMessage);
            }

            return count;
        }

        private static int GetActiveCellCountMinimum(double? min, double? max)
        {
            return min.HasValue && max.HasValue && max.Value > min.Value ? Math.Max(CCellPatternCalculator.MinCellCount, (int)Math.Ceiling(min.Value)) : CCellPatternCalculator.MinCellCount;
        }

        private static int? GetActiveCellCountMaximum(double? max, int minimum)
        {
            return max.HasValue && max.Value >= minimum ? (int)Math.Floor(max.Value) : null;
        }

        private static int GetCellNo(string key)
        {
            if (!key.StartsWith("CELL", StringComparison.OrdinalIgnoreCase))
            {
                return 0;
            }

            var separatorIndex = GetCellKeySeparatorIndex(key);
            return separatorIndex > 4 && int.TryParse(key.AsSpan(4, separatorIndex - 4), NumberStyles.None, CultureInfo.InvariantCulture, out var cellNo) ? cellNo : 0;
        }

        private static string GetCellParameterName(string key)
        {
            var separatorIndex = GetCellKeySeparatorIndex(key);
            return separatorIndex >= 0 && separatorIndex < key.Length - 1 ? key[(separatorIndex + 1)..] : "";
        }

        private static int GetCellKeySeparatorIndex(string key)
        {
            return key.IndexOf('_', 4);
        }

        private static string NormalizeCellScopedParameterName(string parameterName)
        {
            return parameterName.StartsWith("CELL_", StringComparison.OrdinalIgnoreCase) ? parameterName[5..] : parameterName;
        }

        private static bool IsCellPlacementParameterName(string parameterName)
        {
            return parameterName.Equals("ALIGN_TO_1ST_PIXEL_X", StringComparison.OrdinalIgnoreCase) || parameterName.Equals("ALIGN_TO_1ST_PIXEL_Y", StringComparison.OrdinalIgnoreCase) || parameterName.Equals("ROTATION", StringComparison.OrdinalIgnoreCase) || parameterName.Equals("MODEL_TYPE", StringComparison.OrdinalIgnoreCase);
        }

        private static string ToPersistedCellScopedKey(string key)
        {
            return key;
        }

        private static string GetCellScopedGroup(string parameterName)
        {
            if (TryGetShotGroupOverrideMetadata(parameterName, out _, out _, out _, out _, out _, out _, out _))
            {
                return "SHOT_GROUP";
            }

            return "CELL";
        }

        private static string GetCellHistoryItemName(ST_RECIPE_PARAMETER parameter, IReadOnlyList<ST_RECIPE_PARAMETER> parameters)
        {
            var cellNo = GetCellNo(parameter.Key);
            var parameterName = NormalizeCellScopedParameterName(GetCellParameterName(parameter.Key));
            if (TryGetShotGroupOverrideMetadata(parameterName, out var shotGroupName, out _, out _, out var namedDisplayName, out _, out _, out _))
            {
                return $"Cell{cellNo} Shot Group {shotGroupName} {namedDisplayName}";
            }

            return $"Cell{cellNo} {parameter.Name}";
        }

        private static ST_RECIPE_PARAMETER? CreateShotGroupOverrideParameter(int cellNo, string parameterName, string scopedKey, string value)
        {
            if (cellNo <= 0)
            {
                return null;
            }

            if (TryGetShotGroupOverrideMetadata(parameterName, out var shotGroupName, out _, out _, out var namedDisplayName, out var namedUnit, out var namedMin, out var namedMax))
            {
                return new ST_RECIPE_PARAMETER(
                    $"Shot Group {shotGroupName} {namedDisplayName}", 
                    value, 
                    namedUnit, 
                    $"{namedMin.ToString("0.###", CultureInfo.InvariantCulture)} - {namedMax.ToString("0.###", CultureInfo.InvariantCulture)}", 
                    "0", 
                    "CELL", 
                    "SHOT_GROUP", 
                    scopedKey, 
                    "DOE shot-group center correction", 
                    !CAlignShotOffsetData.IsParameterKey(scopedKey), 
                    true, 
                    0, 
                    EN_RECIPE_DATA_TYPE.Double, 
                    0.0, 
                    namedMin, 
                    namedMax
                );
            }

            return null;
        }

        private static bool TryGetShotGroupOverrideMetadata(string parameterName, out string shotGroupName, out int column, out int row, out string displayName, out string unit, out double min, out double max)
        {
            shotGroupName = "";
            column = 0;
            row = 0;
            displayName = "";
            unit = "";
            min = 0.0;
            max = 0.0;
            var normalized = parameterName.Trim().ToUpperInvariant();
            var fields = new[]
            {
                (Suffix: "_RECIPE_OFFSET_X", DisplayName: "Recipe Offset X"),
                (Suffix: "_RECIPE_OFFSET_Y", DisplayName: "Recipe Offset Y"),
                (Suffix: "_ALIGN_OFFSET_X", DisplayName: "Align Offset X"),
                (Suffix: "_ALIGN_OFFSET_Y", DisplayName: "Align Offset Y")
            };
            bool MatchesItem((string Suffix, string DisplayName) candidate)
            {
                return normalized.EndsWith(candidate.Suffix, StringComparison.Ordinal);
            }

            var field = fields.FirstOrDefault(MatchesItem);
            if (string.IsNullOrWhiteSpace(field.Suffix))
            {
                return false;
            }

            var matrixName = normalized[..^field.Suffix.Length];
            var rowStart = 0;
            while (rowStart < matrixName.Length && matrixName[rowStart] is >= 'A' and <= 'Z')
            {
                rowStart++;
            }

            if (rowStart == 0 || rowStart == matrixName.Length || !int.TryParse(matrixName.AsSpan(rowStart), NumberStyles.None, CultureInfo.InvariantCulture, out row) || row <= 0)
            {
                return false;
            }

            try
            {
                foreach (var letter in matrixName.AsSpan(0, rowStart))
                {
                    column = checked((column * 26) + (letter - 'A' + 1));
                }
            }
            catch (OverflowException)
            {
                column = 0;
                return false;
            }

            shotGroupName = matrixName;
            displayName = field.DisplayName;
            unit = "mm";
            min = -100000.0;
            max = 100000.0;
            return column > 0;
        }

        private readonly record struct ST_SHOT_GROUP_PATTERN_BOUNDS(bool IsDefined, int GroupCountX, int GroupCountY);
        private IReadOnlyList<ST_RECIPE_FORM_ITEM> LoadFormItems()
        {
            string[] SelectItem(string header)
            {
                return new[]
                {
                    header
                };
            }

            CCsvParser.ValidateRequiredHeaders(GetFormPath(), "JHMI_RCP", FormHeaders.Select(SelectItem));
            global::Drilling.Common.Managers.ST_RECIPE_FORM_ITEM BuildRecipe(global::System.Collections.Generic.IReadOnlyDictionary<string, string> row, int index)
            {
                return new ST_RECIPE_FORM_ITEM(
                    CCsvParser.Get(row, "TAB"), 
                    CCsvParser.Get(row, "GROUP"), 
                    CCsvParser.Get(row, "NAME"), 
                    GetOrDefault(CCsvParser.Get(row, "DISPLAY NAME"), CCsvParser.Get(row, "NAME")), 
                    GetOrDefault(CCsvParser.Get(row, "CIM NAME"), CCsvParser.Get(row, "NAME")), 
                    ReadDataType(CCsvParser.Get(row, "DATA TYPE")), 
                    CCsvParser.Get(row, "UNIT"), 
                    ReadBool(CCsvParser.Get(row, "SHOW"), true), 
                    ReadBool(CCsvParser.Get(row, "USE"), true), 
                    CCsvParser.Get(row, "VALUE"), 
                    ReadDouble(CCsvParser.Get(row, "SCALE"), 1.0), 
                    ReadDouble(CCsvParser.Get(row, "CHANGE LIMIT"), 0.0), 
                    ReadDouble(CCsvParser.Get(row, "MIN"), 0.0), 
                    ReadDouble(CCsvParser.Get(row, "MAX"), 0.0), 
                    CCsvParser.Get(row, "DESCRIPTION"), 
                    ReadInt(CCsvParser.Get(row, "ORDER"), index + 1), 
                    CCsvParser.GetExtra(row, FormHeaders)
                );
            }

            bool ShouldIncludeItem(global::Drilling.Common.Managers.ST_RECIPE_FORM_ITEM item)
            {
                return !string.IsNullOrWhiteSpace(item.Tab) && !string.IsNullOrWhiteSpace(item.Name);
            }

            return CCsvParser.Read(GetFormPath()).Select(BuildRecipe).Where(ShouldIncludeItem).ToArray();
        }

        private static IReadOnlyList<ST_RECIPE_VALUE> ReadRecipeValues(string path, CancellationToken cancellationToken = default)
        {
            if (!System.IO.File.Exists(path))
            {
                return[];
            }

            var values = new List<ST_RECIPE_VALUE>();
            foreach (var line in CCsvParser.ReadLines(path))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                var value = ParseRecipeLine(line);
                if (value is not null)
                {
                    values.Add(value);
                }
            }

            return values;
        }

        private static ST_RECIPE_VALUE? ParseRecipeLine(string line)
        {
            var fields = SplitCsvLine(line);
            if (fields.Count < 3)
            {
                return null;
            }

            return new ST_RECIPE_VALUE(fields[0], fields[1], fields[2], fields.Skip(3).ToArray());
        }

        private static IReadOnlyList<string> SplitCsvLine(string line)
        {
            var fields = new List<string>();
            var value = new System.Text.StringBuilder();
            var inQuotes = false;
            for (var index = 0; index < line.Length; index++)
            {
                var current = line[index];
                if (current == '"')
                {
                    if (inQuotes && index + 1 < line.Length && line[index + 1] == '"')
                    {
                        value.Append('"');
                        index++;
                    }
                    else
                    {
                        inQuotes = !inQuotes;
                    }
                }
                else if (current == ',' && !inQuotes)
                {
                    fields.Add(value.ToString());
                    value.Clear();
                }
                else
                {
                    value.Append(current);
                }
            }

            fields.Add(value.ToString());
            return fields;
        }

        private string GetFormPath()
        {
            return Path.Combine(_configRoot, "JHMI_RCP.csv");
        }

        private string GetRecipePath(string recipeId)
        {
            return Path.Combine(_recipeDirectory, $"{GetSafeFileName(recipeId)}.csv");
        }

        private static ST_RECIPE_FORM_ITEM? FindFormItem(IReadOnlyList<ST_RECIPE_FORM_ITEM> formItems, ST_RECIPE_PARAMETER parameter)
        {
            bool MatchesItem(global::Drilling.Common.Managers.ST_RECIPE_FORM_ITEM item)
            {
                return item.Tab.Equals(parameter.Tab, StringComparison.OrdinalIgnoreCase) && item.Name.Equals(parameter.Key, StringComparison.OrdinalIgnoreCase);
            }

            if (!string.IsNullOrWhiteSpace(parameter.Tab) && !string.IsNullOrWhiteSpace(parameter.Key))
            {
                var formItem = formItems.FirstOrDefault(MatchesItem);
                if (formItem is not null)
                {
                    return formItem;
                }
            }

            bool MatchesItem2(global::Drilling.Common.Managers.ST_RECIPE_FORM_ITEM item)
            {
                return item.Name.Equals(parameter.Key, StringComparison.OrdinalIgnoreCase);
            }

            if (!string.IsNullOrWhiteSpace(parameter.Key))
            {
                var formItem = formItems.FirstOrDefault(MatchesItem2);
                if (formItem is not null)
                {
                    return formItem;
                }
            }

            bool MatchesItem3(global::Drilling.Common.Managers.ST_RECIPE_FORM_ITEM item)
            {
                return item.Name.Equals(parameter.Name, StringComparison.OrdinalIgnoreCase);
            }

            bool MatchesItem4(global::Drilling.Common.Managers.ST_RECIPE_FORM_ITEM item)
            {
                return item.DisplayName.Equals(parameter.Name, StringComparison.OrdinalIgnoreCase);
            }

            return formItems.FirstOrDefault(MatchesItem3) ?? formItems.FirstOrDefault(MatchesItem4);
        }

        private static EN_RECIPE_DATA_TYPE ReadDataType(string value)
        {
            return value.Trim().ToUpperInvariant() switch
            {
                "INT" => EN_RECIPE_DATA_TYPE.Int,
                "DOUBLE" => EN_RECIPE_DATA_TYPE.Double,
                "BOOL" => EN_RECIPE_DATA_TYPE.Bool,
                _ => EN_RECIPE_DATA_TYPE.String
            };
        }

        private static bool ReadBool(string value, bool defaultValue)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return defaultValue;
            }

            return value.Equals("1", StringComparison.OrdinalIgnoreCase) || value.Equals("TRUE", StringComparison.OrdinalIgnoreCase) || value.Equals("USE", StringComparison.OrdinalIgnoreCase) || value.Equals("ON", StringComparison.OrdinalIgnoreCase);
        }

        private static int ReadInt(string value, int defaultValue)
        {
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ? result : defaultValue;
        }

        private static double ReadDouble(string value, double defaultValue)
        {
            return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result) ? result : defaultValue;
        }

        private static string GetValue(IReadOnlyDictionary<string, string> values, string tab, string name, string defaultValue)
        {
            return values.TryGetValue(CreateKey(tab, name), out var value) ? value : defaultValue;
        }

        private static string CreateKey(string tab, string name)
        {
            return $"{tab.Trim().ToUpperInvariant()}|{name.Trim().ToUpperInvariant()}";
        }

        private static string GetOrDefault(string value, string defaultValue)
        {
            return string.IsNullOrWhiteSpace(value) ? defaultValue : value;
        }

        private static string GetRecipeName(ST_RECIPE_DEFINITION recipe)
        {
            return string.IsNullOrWhiteSpace(recipe.Name) ? recipe.Id : recipe.Name;
        }

        private static string FormatRecipeLine(string tab, string name, string value)
        {
            return string.Join(",", Escape(tab), Escape(name), Escape(value));
        }

        private static void ValidateSavedRecipeFile(string path, IReadOnlyDictionary<string, string> expectedValues, CancellationToken cancellationToken)
        {
            string SelectGroupKey(global::Drilling.Common.Managers.ST_RECIPE_VALUE item)
            {
                return CreateKey(item.Tab, item.Name);
            }

            string SelectKey(global::System.Linq.IGrouping<string, global::Drilling.Common.Managers.ST_RECIPE_VALUE> group)
            {
                return group.Key;
            }

            string SelectValue(global::System.Linq.IGrouping<string, global::Drilling.Common.Managers.ST_RECIPE_VALUE> group)
            {
                return group.Last().Value;
            }

            var actualValues = ReadRecipeValues(path, cancellationToken).GroupBy(SelectGroupKey, StringComparer.OrdinalIgnoreCase).ToDictionary(SelectKey, SelectValue, StringComparer.OrdinalIgnoreCase);
            foreach (var expectedValue in expectedValues)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!actualValues.TryGetValue(expectedValue.Key, out var actualValue))
                {
                    throw new InvalidDataException($"Recipe CSV validation failed. Missing item: {expectedValue.Key}");
                }

                if (!actualValue.Equals(expectedValue.Value, StringComparison.Ordinal))
                {
                    throw new InvalidDataException($"Recipe CSV validation failed. {expectedValue.Key}: expected '{expectedValue.Value}', actual '{actualValue}'");
                }
            }
        }

        private static string Escape(string value)
        {
            if (!value.Contains(',') && !value.Contains('"') && !value.Contains('\r') && !value.Contains('\n'))
            {
                return value;
            }

            return $"\"{value.Replace("\"", "\"\"")}\"";
        }

        private static string GetSafeFileName(string recipeId)
        {
            var invalid = Path.GetInvalidFileNameChars();
            char SelectItem(char character)
            {
                return invalid.Contains(character) ? '_' : character;
            }

            return new string (recipeId.Select(SelectItem).ToArray());
        }

        private static void DeleteIfExists(string path)
        {
            if (System.IO.File.Exists(path))
            {
                System.IO.File.Delete(path);
            }
        }
#endregion

#region Companions
        public Task RenameWithCompanions(string oldRecipeId, string newRecipeId, IReadOnlyCollection<string> selectedFolders, CancellationToken token = default) => ExecuteFileOperation(() => RenameCompanionsCore(oldRecipeId, newRecipeId, selectedFolders, token), token);
        private void RenameCompanionsCore(string oldId, string newId, IReadOnlyCollection<string> selected, CancellationToken token)
        {
            ValidateCopyId(oldId);
            ValidateCopyId(newId);
            token.ThrowIfCancellationRequested();
            if (!System.IO.File.Exists(GetRecipePath(oldId)))
                throw new FileNotFoundException("원본 레시피가 없습니다: " + oldId);
            if (System.IO.File.Exists(GetRecipePath(newId)))
                throw new IOException("이미 존재하는 레시피입니다: " + newId);
            var existing = ListCompanions(newId, newId, token);
            if (existing.Count > 0)
                throw new IOException("변경할 레시피 이름의 부가 폴더가 이미 존재합니다: " + existing[0].SourceName);
            var folders = SelectCompanions(oldId, newId, selected, token);
            var root = Path.GetFullPath(_recipeDirectory);
            var moved = new List<(string Source, string Target)>();
            string? oldName;
            try
            {
                foreach (var folder in folders)
                {
                    token.ThrowIfCancellationRequested();
                    var source = CompanionPath(root, folder.SourceName);
                    var target = CompanionPath(root, folder.TargetName);
                    RejectLink(source);
                    Directory.Move(source, target);
                    moved.Add((source, target));
                }

                oldName = RenameCore(oldId, newId, token, writeHistory: false);
            }
            catch
            {
                foreach (var pair in moved.AsEnumerable().Reverse())
                    Directory.Move(pair.Target, pair.Source);
                throw;
            }

            if (oldName is not null)
                LogCompanionOperation(() => _logManager.WriteRecipeRename(oldName, newId));
        }

        public Task DeleteWithCompanions(string recipeId, IReadOnlyCollection<string> selectedFolders, CancellationToken token = default) => ExecuteFileOperation(() => DeleteCompanionsCore(recipeId, selectedFolders, token), token);
        private void DeleteCompanionsCore(string id, IReadOnlyCollection<string> selected, CancellationToken token)
        {
            ValidateCopyId(id);
            token.ThrowIfCancellationRequested();
            if (!System.IO.File.Exists(GetRecipePath(id)))
                throw new FileNotFoundException("삭제할 레시피가 없습니다: " + id);
            var folders = SelectCompanions(id, id, selected, token);
            var root = Path.GetFullPath(_recipeDirectory);
            var backup = CompanionPath(root, ".recipe-delete-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(backup);
            var ready = false;
            var cleanup = true;
            string? oldName;
            try
            {
                // Back up every selected folder before deleting any of them. A locked input fails
                // here, while the original recipe and all original folders are still intact.
                foreach (var folder in folders)
                    CopyDirectory(CompanionPath(root, folder.SourceName), CompanionPath(backup, folder.SourceName), token);
                ready = true;
                foreach (var folder in folders)
                {
                    token.ThrowIfCancellationRequested();
                    DeleteOwnedDirectory(root, CompanionPath(root, folder.SourceName));
                }

                oldName = DeleteCore(id, token, writeHistory: false);
            }
            catch (Exception operationError)
            {
                if (ready)
                {
                    try
                    {
                        foreach (var folder in folders)
                            RestoreMissing(CompanionPath(backup, folder.SourceName), CompanionPath(root, folder.SourceName));
                    }
                    catch (Exception recoveryError)
                    {
                        cleanup = false;
                        throw new IOException($"레시피 삭제 중 복원하지 못한 부가 데이터가 있습니다. 백업: {backup}", new AggregateException(operationError, recoveryError));
                    }
                }

                throw;
            }
            finally
            {
                if (cleanup)
                    DeleteOwnedDirectory(root, backup);
            }

            if (oldName is not null)
                LogCompanionOperation(() => _logManager.WriteRecipeDelete(oldName));
        }

        private IReadOnlyList<ST_RECIPE_COMPANION_FOLDER> SelectCompanions(string source, string target, IReadOnlyCollection<string> selected, CancellationToken token)
        {
            var available = ListCompanions(source, target, token).ToDictionary(folder => folder.SourceName, StringComparer.OrdinalIgnoreCase);
            return selected.Distinct(StringComparer.OrdinalIgnoreCase).Select(name => available.TryGetValue(name, out var folder) ? folder : throw new IOException("연결된 레시피 부가 폴더가 아닙니다: " + name)).ToArray();
        }

        private static string CompanionPath(string parent, string name)
        {
            var full = Path.GetFullPath(Path.Combine(parent, name));
            if (!string.Equals(Path.GetDirectoryName(full), parent, StringComparison.OrdinalIgnoreCase))
                throw new IOException("레시피 부가 폴더 경로가 올바르지 않습니다.");
            return full;
        }

        private static void RestoreMissing(string backup, string original)
        {
            if (Directory.Exists(original))
                RejectLink(original);
            Directory.CreateDirectory(original);
            foreach (var file in Directory.EnumerateFiles(backup))
            {
                var target = Path.Combine(original, Path.GetFileName(file));
                if (!System.IO.File.Exists(target))
                    System.IO.File.Copy(file, target, overwrite: false);
            }

            foreach (var child in Directory.EnumerateDirectories(backup))
                RestoreMissing(child, Path.Combine(original, Path.GetFileName(child)));
        }

        private static void LogCompanionOperation(Action write)
        {
            try
            {
                write();
            }
            catch (Exception error)
            {
                try
                {
                    Trace.TraceError($"Recipe companion operation completed, but logging failed: {error}");
                }
                catch
                { /* Diagnostics cannot invalidate an already completed file operation. */
                }
            }
        }
#endregion

#region Copy
        public Task<IReadOnlyList<ST_RECIPE_COMPANION_FOLDER>> ListCompanionFolders(string sourceRecipeId, string newRecipeId, CancellationToken token = default) => ExecuteFileOperation(() => ListCompanions(sourceRecipeId, newRecipeId, token), token);
        private IReadOnlyList<ST_RECIPE_COMPANION_FOLDER> ListCompanions(string sourceRecipeId, string newRecipeId, CancellationToken token)
        {
            ValidateCopyId(sourceRecipeId);
            ValidateCopyId(newRecipeId);
            token.ThrowIfCancellationRequested();
            if (!Directory.Exists(_recipeDirectory))
                return[];
            var prefix = sourceRecipeId + "_";
            return Directory.EnumerateDirectories(_recipeDirectory).Select(Path.GetFileName).Where(name => name is not null && name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).OrderBy(name => name, StringComparer.OrdinalIgnoreCase).Select(name => new ST_RECIPE_COMPANION_FOLDER(name!, newRecipeId + name![sourceRecipeId.Length..])).ToArray();
        }

        public Task CreateCopy(ST_RECIPE_DEFINITION recipe, string sourceRecipeId, IReadOnlyCollection<string> selectedFolders, CancellationToken token = default) => ExecuteFileOperation(() => CreateCopyCore(recipe, sourceRecipeId, selectedFolders, token), token);
        private void CreateCopyCore(ST_RECIPE_DEFINITION recipe, string sourceRecipeId, IReadOnlyCollection<string> selectedFolders, CancellationToken token)
        {
            ValidateCopyId(sourceRecipeId);
            ValidateCopyId(recipe.Id);
            token.ThrowIfCancellationRequested();
            if (sourceRecipeId.Equals(recipe.Id, StringComparison.OrdinalIgnoreCase))
                throw new IOException("원본과 새 레시피 이름이 같습니다.");
            if (!System.IO.File.Exists(GetRecipePath(sourceRecipeId)))
                throw new FileNotFoundException("원본 레시피가 없습니다: " + sourceRecipeId);
            var destination = GetRecipePath(recipe.Id);
            if (System.IO.File.Exists(destination))
                throw new IOException("이미 존재하는 레시피입니다: " + recipe.Id);
            // A declined copy must not silently activate an orphaned offset folder from a deleted recipe.
            var existingFolders = ListCompanions(recipe.Id, recipe.Id, token);
            if (existingFolders.Count != 0)
                throw new IOException("새 레시피 이름의 부가 폴더가 이미 존재합니다: " + existingFolders[0].SourceName);
            var available = ListCompanions(sourceRecipeId, recipe.Id, token).ToDictionary(item => item.SourceName, StringComparer.OrdinalIgnoreCase);
            var folders = selectedFolders.Distinct(StringComparer.OrdinalIgnoreCase).Select(name => available.TryGetValue(name, out var folder) ? folder : throw new IOException("원본 레시피의 부가 폴더를 찾을 수 없습니다: " + name)).ToArray();
            foreach (var folder in folders)
                if (Directory.Exists(Path.Combine(_recipeDirectory, folder.TargetName)) || System.IO.File.Exists(Path.Combine(_recipeDirectory, folder.TargetName)))
                    throw new IOException("새 레시피의 부가 폴더가 이미 존재합니다: " + folder.TargetName);
            var root = Path.GetFullPath(_recipeDirectory);
            var staging = Path.Combine(root, ".recipe-create-" + Guid.NewGuid().ToString("N"));
            var committed = new List<string>();
            Directory.CreateDirectory(staging);
            try
            {
                var stagedRecipe = Path.Combine(staging, recipe.Id + ".csv");
                SaveCore(recipe, token, stagedRecipe, writeHistory: false);
                foreach (var folder in folders)
                {
                    var excludeApplyHistory = folder.SourceName.EndsWith(
                        "_reviewoffset",
                        StringComparison.OrdinalIgnoreCase);
                    CopyDirectory(
                        Path.Combine(root, folder.SourceName),
                        Path.Combine(staging, folder.TargetName),
                        token,
                        excludeApplyHistory);
                }
                token.ThrowIfCancellationRequested();
                foreach (var folder in folders)
                {
                    var target = Path.GetFullPath(Path.Combine(root, folder.TargetName));
                    var stagedFolder = Path.GetFullPath(Path.Combine(staging, folder.TargetName));
                    if (!string.Equals(Path.GetDirectoryName(target), root, StringComparison.OrdinalIgnoreCase) || !string.Equals(Path.GetDirectoryName(stagedFolder), staging, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("레시피 부가 폴더 경로가 올바르지 않습니다.");
                    Directory.Move(stagedFolder, target);
                    committed.Add(target);
                }

                // Publish the recipe only after every selected folder is ready; never overwrite a destination.
                System.IO.File.Move(stagedRecipe, destination, overwrite: false);
            }
            catch
            {
                foreach (var directory in committed)
                    DeleteOwnedDirectory(root, directory);
                throw;
            }
            finally
            {
                DeleteOwnedDirectory(root, staging);
            }

            _logManager.WriteRecipeCreate(GetRecipeName(recipe));
        }

        private static void CopyDirectory(
            string source,
            string destination,
            CancellationToken token,
            bool excludeReviewApplyHistory = false)
        {
            token.ThrowIfCancellationRequested();
            RejectLink(source);
            Directory.CreateDirectory(destination);
            foreach (var file in Directory.EnumerateFiles(source))
            {
                token.ThrowIfCancellationRequested();
                RejectLink(file);
                System.IO.File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: false);
            }

            foreach (var child in Directory.EnumerateDirectories(source))
            {
                if (excludeReviewApplyHistory
                    && Path.GetFileName(child).Equals("_apply", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                CopyDirectory(
                    child,
                    Path.Combine(destination, Path.GetFileName(child)),
                    token,
                    excludeReviewApplyHistory);
            }
        }

        private static void RejectLink(string path)
        {
            if ((System.IO.File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("링크 폴더 또는 링크 파일은 레시피 부가 데이터로 복사할 수 없습니다: " + path);
        }

        private static void DeleteOwnedDirectory(string root, string path)
        {
            // Only newly created, direct children of this recipe directory can be rolled back.
            var full = Path.GetFullPath(path);
            if (!string.Equals(Path.GetDirectoryName(full), root, StringComparison.OrdinalIgnoreCase))
                throw new IOException("레시피 작업 경로가 올바르지 않습니다.");
            if (Directory.Exists(full))
                Directory.Delete(full, recursive: true);
        }

        private static void ValidateCopyId(string id)
        {
            if (string.IsNullOrWhiteSpace(id) || id is "." or ".." || !GetSafeFileName(id).Equals(id, StringComparison.Ordinal))
                throw new ArgumentException("레시피 이름이 올바르지 않습니다.", nameof(id));
        }
#endregion

#region ProtectWindow
        public override Task<int> IncrementProtectWindowCount(string recipeId, CancellationToken cancellationToken = default) => ChangeProtectWindowCount(recipeId, current => checked(CProtectWindowRecipeData.ParseCount(current) + 1), cancellationToken);
        public override Task<int> ResetProtectWindowCount(string recipeId, CancellationToken cancellationToken = default) => SetProtectWindowCount(recipeId, 0, cancellationToken);
        public override Task<int> SetProtectWindowCount(string recipeId, int count, CancellationToken cancellationToken = default) => count < 0 ? Task.FromException<int>(new ArgumentOutOfRangeException(nameof(count))) : ChangeProtectWindowCount(recipeId, _ => count, cancellationToken);
        private Task<int> ChangeProtectWindowCount(string recipeId, Func<string, int> change, CancellationToken cancellationToken) => ExecuteFileOperation(() =>
        {
            var path = GetRecipePath(recipeId);
            if (!System.IO.File.Exists(path))
                throw new FileNotFoundException("Recipe not found for Protect Window count.", path);
            var lines = CCsvParser.ReadLines(path);
            bool IsCount(global::Drilling.Common.Managers.ST_RECIPE_VALUE? value) => value is not null && value.Tab.Equals(CProtectWindowRecipeData.Tab, StringComparison.OrdinalIgnoreCase) && value.Name.Equals(CProtectWindowRecipeData.CurrentCountKey, StringComparison.OrdinalIgnoreCase);
            var current = lines.Select(ParseRecipeLine).LastOrDefault(IsCount)?.Value ?? "0";
            var next = change(current);
            var updated = new List<string>(lines.Count + 1);
            var inserted = false;
            foreach (var line in lines)
            {
                if (!IsCount(ParseRecipeLine(line)))
                    updated.Add(line);
                else if (!inserted)
                {
                    updated.Add(CountLine());
                    inserted = true;
                }
            }

            if (!inserted)
                updated.Add(CountLine());
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                // Commit only the counter row; retain every other value and avoid a full recipe/history reload.
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                {
                    using (var writer = new StreamWriter(stream, new UTF8Encoding(true), leaveOpen: true))
                    {
                        foreach (var line in updated)
                            writer.WriteLine(line);
                        writer.Flush();
                    }

                    stream.Flush(flushToDisk: true);
                }

                cancellationToken.ThrowIfCancellationRequested();
                System.IO.File.Move(temporary, path, overwrite: true);
                return next;
            }
            finally
            {
                if (System.IO.File.Exists(temporary))
                    System.IO.File.Delete(temporary);
            }

            string CountLine() => FormatRecipeLine(CProtectWindowRecipeData.Tab, CProtectWindowRecipeData.CurrentCountKey, next.ToString(CultureInfo.InvariantCulture));
        }, cancellationToken);
#endregion
    }
}
