using Drilling.Common.Log;

namespace Drilling.Common.Product;
public sealed class CProductManager(CProductFileBase productFile, CLogManager? logManager = null)
{
    private readonly SemaphoreSlim _updateGate = new(1, 1);
    private ST_PRODUCT_DATA? _current;
    private int _hasActiveDataLoadError;
    public ST_PRODUCT_DATA? Current
    {
        get
        {
            return Volatile.Read(ref _current);
        }
    }

    public bool HasActiveDataLoadError
    {
        get
        {
            return Volatile.Read(ref _hasActiveDataLoadError) != 0;
        }
    }

    public async Task<ST_PRODUCT_DATA?> LoadActive(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _updateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            try
            {
                _current = await productFile.LoadActive(cancellationToken).ConfigureAwait(false);
                Volatile.Write(ref _hasActiveDataLoadError, 0);
                return _current;
            }
            catch
            {
                Volatile.Write(ref _hasActiveDataLoadError, 1);
                throw;
            }
        }
        finally
        {
            _updateGate.Release();
        }
    }

    public async Task<ST_PRODUCT_DATA?> RecoverInterrupted(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _updateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        bool MatchesState(global::Drilling.Common.Product.ST_PRODUCT_HEAD_RESULT head)
        {
            return head.State == EN_PRODUCT_HEAD_STATE.Running;
        }

        try
        {
            var product = _current;
            if (product is null)
            {
                return null;
            }

            // A Glass that was only created has not started processing yet, so it is
            // still valid after an application restart.  Only a running Glass is an
            // interrupted process that must be recovered as stopped.
            var productInterrupted = product.State == EN_PRODUCT_STATE.Running;
            var alignInterrupted = product.Align.State == EN_PRODUCT_PROCESS_STATE.Running;
            var distortionInterrupted = product.Distortion.State == EN_PRODUCT_PROCESS_STATE.Running;
            var reviewInterrupted = product.Review.State == EN_PRODUCT_PROCESS_STATE.Running;
            var headInterrupted = product.Heads.Any(MatchesState);
            if (!productInterrupted && !alignInterrupted && !distortionInterrupted && !reviewInterrupted && !headInterrupted)
            {
                return product;
            }

            var now = DateTimeOffset.Now;
            global::Drilling.Common.Product.ST_PRODUCT_HEAD_RESULT SelectValue(global::Drilling.Common.Product.ST_PRODUCT_HEAD_RESULT head)
            {
                return head.State == EN_PRODUCT_HEAD_STATE.Running ? head with
                {
                    State = EN_PRODUCT_HEAD_STATE.Stopped,
                    Result = EN_PRODUCT_RESULT.NG,
                    Message = "Interrupted by application restart.",
                    CompletedAt = now
                }

                : head;
            }

            var next = product with
            {
                Product = productInterrupted ? SetProductState(product.Product, EN_PRODUCT_STATE.Stopped, EN_PRODUCT_RESULT.NG, product.StartedAt, now) : product.Product,
                Align = alignInterrupted ? product.Align with
                {
                    State = EN_PRODUCT_PROCESS_STATE.Stopped,
                    Result = "INTERRUPTED"
                }

                : product.Align,
                Distortion = distortionInterrupted ? product.Distortion with
                {
                    State = EN_PRODUCT_PROCESS_STATE.Stopped,
                    Result = "INTERRUPTED"
                }

                : product.Distortion,
                Review = reviewInterrupted ? product.Review with
                {
                    State = EN_PRODUCT_PROCESS_STATE.Stopped
                }

                : product.Review,
                Runtime = product.Runtime with
                {
                    Heads = product.Heads.Select(SelectValue).ToArray()
                }
            };
            return await PersistAndLog(next, "RECOVERY", "Interrupted Product state recovered during application startup.", cancellationToken);
        }
        finally
        {
            _updateGate.Release();
        }
    }

    public async Task<ST_PRODUCT_DATA> CreateProduct(
        string processId, 
        string glassId, 
        string lotId, 
        string recipeId, 
        IReadOnlyDictionary<string, string> parameters, 
        IReadOnlyDictionary<int, int> headPointCounts, 
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _updateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        int SelectKey(global::System.Collections.Generic.KeyValuePair<int, int> item)
        {
            return item.Key;
        }

        global::Drilling.Common.Product.ST_PRODUCT_HEAD_RESULT BuildProduct(global::System.Collections.Generic.KeyValuePair<int, int> item)
        {
            return new ST_PRODUCT_HEAD_RESULT(item.Key, item.Value > 0 ? EN_PRODUCT_HEAD_STATE.Ready : EN_PRODUCT_HEAD_STATE.Disabled, Math.Max(0, item.Value), 0, EN_PRODUCT_RESULT.Pending, "", "", null, null);
        }

        try
        {
            if (HasActiveDataLoadError)
            {
                throw new InvalidOperationException("A new Product cannot be created because ActiveProduct.csv failed to load. Back up or repair the file and restart the application.");
            }

            var activeProduct = _current;
            if (activeProduct is null)
            {
                try
                {
                    activeProduct = await productFile.LoadActive(cancellationToken).ConfigureAwait(false);
                    Volatile.Write(ref _hasActiveDataLoadError, 0);
                }
                catch
                {
                    Volatile.Write(ref _hasActiveDataLoadError, 1);
                    throw;
                }

                if (activeProduct is not null)
                {
                    Volatile.Write(ref _current, activeProduct);
                }
            }

            if (activeProduct is not null && activeProduct.State != EN_PRODUCT_STATE.Completed)
            {
                throw new InvalidOperationException($"A Glass is already active: {activeProduct.GlassId}. Delete the current Glass before creating another one.");
            }

            var resolvedGlassId = ChooseGlassId(processId, glassId, parameters);
            var now = DateTimeOffset.Now;
            var heads = headPointCounts.OrderBy(SelectKey).Select(BuildProduct).ToArray();
            var next = new ST_PRODUCT_DATA(
                CreateProductInfo(resolvedGlassId, lotId, processId, recipeId, parameters, now), 
                new ST_PRODUCT_RECIPE_DATA(new Dictionary<string, string>(parameters, StringComparer.OrdinalIgnoreCase)), 
                new ST_PRODUCT_ALIGN_DATA(EN_PRODUCT_PROCESS_STATE.NotStarted, "PENDING", null, null, null), 
                new ST_PRODUCT_DISTORTION_DATA(EN_PRODUCT_PROCESS_STATE.NotStarted, "PENDING", null), 
                new ST_PRODUCT_REVIEW_DATA(EN_PRODUCT_PROCESS_STATE.NotStarted, null, []), 
                new ST_PRODUCT_RUNTIME_DATA(heads)
            );
            return await PersistAndLog(next, activeProduct is null ? "CREATE" : "REPLACE", activeProduct is null ? "Product created." : $"Completed Glass {activeProduct.GlassId} replaced with a new Glass.", cancellationToken);
        }
        finally
        {
            _updateGate.Release();
        }
    }

    public async Task<ST_PRODUCT_DATA> PrepareProductForProcess(
        string productId, 
        string processId, 
        string recipeId, 
        IReadOnlyDictionary<string, string> parameters, 
        IReadOnlyDictionary<int, int> headPointCounts, 
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _updateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var product = GetCurrent(productId);
            if (product.State is EN_PRODUCT_STATE.Running or EN_PRODUCT_STATE.Scrapped)
            {
                throw new InvalidOperationException($"Product '{productId}' cannot be assigned to a process while its state is {product.State}.");
            }

            var heads = headPointCounts.OrderBy(item => item.Key).Select(item => new ST_PRODUCT_HEAD_RESULT(item.Key, item.Value > 0 ? EN_PRODUCT_HEAD_STATE.Ready : EN_PRODUCT_HEAD_STATE.Disabled, Math.Max(0, item.Value), 0, EN_PRODUCT_RESULT.Pending, "", "", null, null)).ToArray();
            var next = product with
            {
                Product = SetProductState(product.Product.Set("PROCESS_ID", processId).Set("PPID", recipeId), EN_PRODUCT_STATE.Created, EN_PRODUCT_RESULT.Pending, null, null),
                Recipe = new ST_PRODUCT_RECIPE_DATA(new Dictionary<string, string>(parameters, StringComparer.OrdinalIgnoreCase)),
                Align = new ST_PRODUCT_ALIGN_DATA(EN_PRODUCT_PROCESS_STATE.NotStarted, "PENDING", null, null, null),
                Distortion = new ST_PRODUCT_DISTORTION_DATA(EN_PRODUCT_PROCESS_STATE.NotStarted, "PENDING", null),
                Review = new ST_PRODUCT_REVIEW_DATA(EN_PRODUCT_PROCESS_STATE.NotStarted, null, []),
                Runtime = new ST_PRODUCT_RUNTIME_DATA(heads)
            };
            return await PersistAndLog(next, "PROCESS ASSIGN", $"Product assigned to process {processId}.", cancellationToken);
        }
        finally
        {
            _updateGate.Release();
        }
    }

    public async Task<ST_PRODUCT_DATA> StartProduct(string productId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _updateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var product = GetCurrent(productId);
            var now = DateTimeOffset.Now;
            global::Drilling.Common.Product.ST_PRODUCT_HEAD_RESULT SelectItem(global::Drilling.Common.Product.ST_PRODUCT_HEAD_RESULT head)
            {
                return head.TotalPoints > 0 ? head with
                {
                    State = EN_PRODUCT_HEAD_STATE.Running,
                    StartedAt = head.StartedAt ?? now
                }

                : head with
                {
                    State = EN_PRODUCT_HEAD_STATE.Disabled
                };
            }

            var next = product with
            {
                Product = SetProductState(product.Product, EN_PRODUCT_STATE.Running, EN_PRODUCT_RESULT.Pending, product.StartedAt ?? now, null),
                Runtime = product.Runtime with
                {
                    Heads = product.Heads.Select(SelectItem).ToArray()
                }
            };
            return await PersistAndLog(next, "START", "Product started.", cancellationToken);
        }
        finally
        {
            _updateGate.Release();
        }
    }

    public async Task<ST_PRODUCT_DATA> SetProductInfo(string productId, IReadOnlyDictionary<string, string> values, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _updateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        bool ShouldIncludeItem(string key)
        {
            return !HostWritableProductFieldNames.Contains(key);
        }

        try
        {
            var invalidFields = values.Keys.Where(ShouldIncludeItem).ToArray();
            if (invalidFields.Length > 0)
            {
                throw new ArgumentException($"Product information contains fields that cannot be changed by SetProductInfo: {string.Join(", ", invalidFields)}", nameof(values));
            }

            var product = GetCurrent(productId);
            var productInfo = product.Product;
            foreach (var item in values)
            {
                productInfo = productInfo.Set(item.Key, item.Value);
            }

            var next = product with
            {
                Product = productInfo
            };
            return await PersistAndLog(next, "PRODUCT INFO UPDATE", "Product information updated.", cancellationToken);
        }
        finally
        {
            _updateGate.Release();
        }
    }

    public async Task<ST_PRODUCT_DATA> SetHeadRunning(string productId, int headNo, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _updateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var product = GetCurrent(productId);
            var now = DateTimeOffset.Now;
            global::Drilling.Common.Product.ST_PRODUCT_HEAD_RESULT SelectItem(global::Drilling.Common.Product.ST_PRODUCT_HEAD_RESULT head)
            {
                return head.HeadNo == headNo ? head with
                {
                    State = EN_PRODUCT_HEAD_STATE.Running,
                    StartedAt = head.StartedAt ?? now
                }

                : head;
            }

            var next = product with
            {
                Runtime = product.Runtime with
                {
                    Heads = product.Heads.Select(SelectItem).ToArray()
                }
            };
            return await PersistAndLog(next, "HEAD START", $"Head {headNo:00} started.", cancellationToken);
        }
        finally
        {
            _updateGate.Release();
        }
    }

    public async Task<ST_PRODUCT_DATA> SetHeadResult(string productId, int headNo, bool isOk, string errorCode = "", string message = "", CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _updateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var product = GetCurrent(productId);
            var now = DateTimeOffset.Now;
            global::Drilling.Common.Product.ST_PRODUCT_HEAD_RESULT SelectItem(global::Drilling.Common.Product.ST_PRODUCT_HEAD_RESULT head)
            {
                return head.HeadNo == headNo ? head with
                {
                    State = isOk ? EN_PRODUCT_HEAD_STATE.Completed : EN_PRODUCT_HEAD_STATE.Error,
                    CompletedPoints = isOk ? head.TotalPoints : head.CompletedPoints,
                    Result = isOk ? EN_PRODUCT_RESULT.OK : EN_PRODUCT_RESULT.NG,
                    ErrorCode = errorCode,
                    Message = message,
                    StartedAt = head.StartedAt ?? now,
                    CompletedAt = now
                }

                : head;
            }

            var next = product with
            {
                Runtime = product.Runtime with
                {
                    Heads = product.Heads.Select(SelectItem).ToArray()
                }
            };
            return await PersistAndLog(next, isOk ? "HEAD COMPLETE" : "HEAD ERROR", $"Head {headNo:00}: {(isOk ? "OK" : "NG")} {message}".Trim(), cancellationToken);
        }
        finally
        {
            _updateGate.Release();
        }
    }

    public async Task<ST_PRODUCT_DATA> SetAlign(string productId, ST_PRODUCT_ALIGN_DATA align, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _updateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var product = GetCurrent(productId);
            var next = product with
            {
                Align = align
            };
            return await PersistAndLog(next, "ALIGN UPDATE", $"Align state={align.State}, result={align.Result}.", cancellationToken);
        }
        finally
        {
            _updateGate.Release();
        }
    }

    public async Task<ST_PRODUCT_DATA> SetDistortion(string productId, ST_PRODUCT_DISTORTION_DATA distortion, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _updateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var product = GetCurrent(productId);
            var next = product with
            {
                Distortion = distortion
            };
            return await PersistAndLog(next, "DISTORTION UPDATE", $"Distortion state={distortion.State}, result={distortion.Result}.", cancellationToken);
        }
        finally
        {
            _updateGate.Release();
        }
    }

    public async Task<ST_PRODUCT_DATA> SetAlignment(string productId, ST_PRODUCT_ALIGN_DATA align, ST_PRODUCT_DISTORTION_DATA distortion, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _updateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var product = GetCurrent(productId);
            var next = product with
            {
                Align = align,
                Distortion = distortion
            };
            return await PersistAndLog(next, "ALIGNMENT UPDATE", $"Align state={align.State}, result={align.Result}; Distortion state={distortion.State}, result={distortion.Result}.", cancellationToken);
        }
        finally
        {
            _updateGate.Release();
        }
    }

    public async Task<ST_PRODUCT_DATA> SetReviewRunning(string productId, ST_PRODUCT_REVIEW_DATA review, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _updateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var product = GetCurrent(productId);
            var next = product with
            {
                Review = review with
                {
                    State = EN_PRODUCT_PROCESS_STATE.Running
                }
            };
            return await PersistAndLog(next, "REVIEW START", "Review started.", cancellationToken);
        }
        finally
        {
            _updateGate.Release();
        }
    }

    public async Task<ST_PRODUCT_DATA> SetReviewProgress(string productId, ST_PRODUCT_REVIEW_DATA review, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _updateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var product = GetCurrent(productId);
            var next = product with
            {
                Review = review with
                {
                    State = EN_PRODUCT_PROCESS_STATE.Running
                }
            };
            await productFile.SaveActive(next, cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _current, next);
            return next;
        }
        finally
        {
            _updateGate.Release();
        }
    }

    public async Task<ST_PRODUCT_DATA> SetReviewCompleted(string productId, DateTimeOffset savedAt, IReadOnlyList<ST_PRODUCT_REVIEW_RESULT> results, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _updateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var product = GetCurrent(productId);
            var next = product with
            {
                Review = product.Review with
                {
                    State = EN_PRODUCT_PROCESS_STATE.Completed,
                    SavedAt = savedAt,
                    Results = results.ToArray()
                }
            };
            return await PersistAndLog(next, "REVIEW COMPLETE", $"Review completed. Result count={results.Count}.", cancellationToken);
        }
        finally
        {
            _updateGate.Release();
        }
    }

    public async Task<ST_PRODUCT_DATA> SetReviewStopped(string productId, string message, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _updateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var product = GetCurrent(productId);
            var next = product with
            {
                Review = product.Review with
                {
                    State = EN_PRODUCT_PROCESS_STATE.Stopped
                }
            };
            return await PersistAndLog(next, "REVIEW STOP", message, cancellationToken);
        }
        finally
        {
            _updateGate.Release();
        }
    }

    public async Task<ST_PRODUCT_DATA> SetReviewError(string productId, string message, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _updateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var product = GetCurrent(productId);
            var next = product with
            {
                Review = product.Review with
                {
                    State = EN_PRODUCT_PROCESS_STATE.Error
                }
            };
            return await PersistAndLog(next, "REVIEW ERROR", message, cancellationToken);
        }
        finally
        {
            _updateGate.Release();
        }
    }

    public async Task<ST_PRODUCT_DATA> CompleteProduct(string productId, bool isOk, bool processExecuted, string message, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _updateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var product = GetCurrent(productId);
            var now = DateTimeOffset.Now;
            global::Drilling.Common.Product.ST_PRODUCT_HEAD_RESULT SelectItem(global::Drilling.Common.Product.ST_PRODUCT_HEAD_RESULT head)
            {
                return head.TotalPoints <= 0 ? head with
                {
                    State = EN_PRODUCT_HEAD_STATE.Disabled
                }

                : !processExecuted ? head with
                {
                    State = EN_PRODUCT_HEAD_STATE.Disabled,
                    CompletedPoints = 0,
                    Result = EN_PRODUCT_RESULT.Skipped,
                    ErrorCode = "",
                    Message = "Process step skipped.",
                    CompletedAt = now
                }

                : head with
                {
                    State = isOk ? EN_PRODUCT_HEAD_STATE.Completed : EN_PRODUCT_HEAD_STATE.Error,
                    CompletedPoints = isOk ? head.TotalPoints : head.CompletedPoints,
                    Result = isOk ? EN_PRODUCT_RESULT.OK : EN_PRODUCT_RESULT.NG,
                    ErrorCode = isOk ? "" : "1",
                    Message = message,
                    CompletedAt = now
                };
            }

            var next = product with
            {
                Product = SetProductState(product.Product, EN_PRODUCT_STATE.Completed, !processExecuted ? EN_PRODUCT_RESULT.Skipped : isOk ? EN_PRODUCT_RESULT.OK : EN_PRODUCT_RESULT.NG, product.StartedAt, now),
                Runtime = product.Runtime with
                {
                    Heads = product.Heads.Select(SelectItem).ToArray()
                }
            };
            return await PersistAndLog(next, "COMPLETE", message, cancellationToken);
        }
        finally
        {
            _updateGate.Release();
        }
    }

    public async Task<ST_PRODUCT_DATA> StopProduct(string productId, string message, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _updateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var product = GetCurrent(productId);
            var now = DateTimeOffset.Now;
            global::Drilling.Common.Product.ST_PRODUCT_HEAD_RESULT SelectItem(global::Drilling.Common.Product.ST_PRODUCT_HEAD_RESULT head)
            {
                return head.State == EN_PRODUCT_HEAD_STATE.Running ? head with
                {
                    State = EN_PRODUCT_HEAD_STATE.Stopped,
                    Result = EN_PRODUCT_RESULT.NG,
                    Message = message,
                    CompletedAt = now
                }

                : head;
            }

            var next = product with
            {
                Product = SetProductState(product.Product, EN_PRODUCT_STATE.Stopped, EN_PRODUCT_RESULT.NG, product.StartedAt, now),
                Runtime = product.Runtime with
                {
                    Heads = product.Heads.Select(SelectItem).ToArray()
                }
            };
            return await PersistAndLog(next, "STOP", message, cancellationToken);
        }
        finally
        {
            _updateGate.Release();
        }
    }

    public async Task<ST_PRODUCT_DATA> SetError(string productId, string message, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _updateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var product = GetCurrent(productId);
            var now = DateTimeOffset.Now;
            global::Drilling.Common.Product.ST_PRODUCT_HEAD_RESULT SelectItem(global::Drilling.Common.Product.ST_PRODUCT_HEAD_RESULT head)
            {
                return head.State == EN_PRODUCT_HEAD_STATE.Running ? head with
                {
                    State = EN_PRODUCT_HEAD_STATE.Error,
                    Result = EN_PRODUCT_RESULT.NG,
                    ErrorCode = "1",
                    Message = message,
                    CompletedAt = now
                }

                : head;
            }

            var next = product with
            {
                Product = SetProductState(product.Product, EN_PRODUCT_STATE.Error, EN_PRODUCT_RESULT.NG, product.StartedAt, now),
                Runtime = product.Runtime with
                {
                    Heads = product.Heads.Select(SelectItem).ToArray()
                }
            };
            return await PersistAndLog(next, "ERROR", message, cancellationToken);
        }
        finally
        {
            _updateGate.Release();
        }
    }

    public async Task<ST_PRODUCT_DATA> ScrapProduct(string productId, string reason, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _updateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var product = GetCurrent(productId);
            var next = product with
            {
                Product = SetProductState(product.Product, EN_PRODUCT_STATE.Scrapped, EN_PRODUCT_RESULT.NG, product.StartedAt, DateTimeOffset.Now)
            };
            return await PersistAndLog(next, "SCRAP", reason, cancellationToken);
        }
        finally
        {
            _updateGate.Release();
        }
    }

    public async Task DeleteProduct(string productId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _updateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var product = GetCurrent(productId);
            if (product.State == EN_PRODUCT_STATE.Running)
            {
                throw new InvalidOperationException($"Running Glass '{product.GlassId}' cannot be deleted.");
            }

            await productFile.ClearActive(cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _current, null);
            try
            {
                await productFile.AppendHistory(new ST_PRODUCT_HISTORY(DateTimeOffset.Now, product.GlassId, product.ProcessId, product.RecipeId, "DELETE", product.State.ToString().ToUpperInvariant(), product.Result.ToString().ToUpperInvariant(), "Product deleted."), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception historyException)
            {
                try
                {
                    logManager?.WriteProductEvent(product.GlassId, "HISTORY ERROR", product.State.ToString().ToUpperInvariant(), product.Result.ToString().ToUpperInvariant(), $"DELETE: {historyException.Message}");
                }
                catch
                {
                // The active Product was already deleted; logging must not report the deletion as failed.
                }
            }
        }
        finally
        {
            _updateGate.Release();
        }
    }

    public async Task ReleaseCompletedProduct(string productId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _updateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var product = GetCurrent(productId);
            if (product.State != EN_PRODUCT_STATE.Completed)
            {
                throw new InvalidOperationException($"Only a completed Product can be released. Current state={product.State}");
            }

            await productFile.ClearActive(cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _current, null);
        }
        finally
        {
            _updateGate.Release();
        }
    }

    public async Task<IReadOnlyList<ST_PRODUCT_HISTORY>> LoadHistory(int maxRows = 100, int days = 14, CancellationToken cancellationToken = default)
    {
        await _updateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await productFile.LoadHistory(maxRows, days, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _updateGate.Release();
        }
    }

    private ST_PRODUCT_DATA GetCurrent(string productId)
    {
        if (_current is null)
        {
            throw new InvalidOperationException("Product is not loaded.");
        }

        if (!_current.GlassId.Equals(productId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Glass ID mismatch. Current={_current.GlassId}, Input={productId}");
        }

        return _current;
    }

    private async Task<ST_PRODUCT_DATA> PersistAndLog(ST_PRODUCT_DATA product, string action, string detail, CancellationToken cancellationToken)
    {
        await productFile.SaveActive(product, cancellationToken).ConfigureAwait(false);
        Volatile.Write(ref _current, product);
        var history = new ST_PRODUCT_HISTORY(DateTimeOffset.Now, product.GlassId, product.ProcessId, product.RecipeId, action, product.State.ToString().ToUpperInvariant(), product.Result.ToString().ToUpperInvariant(), detail);
        try
        {
            await productFile.AppendHistory(history, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception historyException)
        {
            try
            {
                logManager?.WriteProductEvent(product.GlassId, "HISTORY ERROR", product.State.ToString().ToUpperInvariant(), product.Result.ToString().ToUpperInvariant(), $"{action}: {historyException.Message}");
            }
            catch
            {
            // Active Product is already committed; logging must not turn it into a failed transition.
            }
        }

        try
        {
            logManager?.WriteProductEvent(product.GlassId, action, product.State.ToString().ToUpperInvariant(), product.Result.ToString().ToUpperInvariant(), detail);
        }
        catch
        {
        // Active Product is already committed; logging must not turn it into a failed transition.
        }

        return product;
    }

    private static ST_PRODUCT_INFO CreateProductInfo(string hGlassId, string lotId, string processId, string ppid, IReadOnlyDictionary<string, string> parameters, DateTimeOffset createdAt)
    {
        string SelectName(string name)
        {
            return name;
        }

        string SelectValue(string arg0)
        {
            return "";
        }

        var values = ProductFieldNames.ToDictionary(SelectName, SelectValue, StringComparer.OrdinalIgnoreCase);
        foreach (var item in parameters)
        {
            if (HostWritableProductFieldNames.Contains(item.Key))
            {
                values[item.Key] = item.Value?.Trim() ?? "";
            }
        }

        values["H_GLASSID"] = hGlassId;
        values["LOTID"] = lotId;
        values["PROCESS_ID"] = processId;
        values["PPID"] = ppid;
        values["PRODUCT_STATE"] = EN_PRODUCT_STATE.Created.ToString().ToUpperInvariant();
        values["JUDGEMENT"] = EN_PRODUCT_RESULT.Pending.ToString().ToUpperInvariant();
        values["CREATED_AT"] = createdAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        return new ST_PRODUCT_INFO(values);
    }

    private static ST_PRODUCT_INFO SetProductState(ST_PRODUCT_INFO product, EN_PRODUCT_STATE state, EN_PRODUCT_RESULT result, DateTimeOffset? startedAt, DateTimeOffset? completedAt)
    {
        return product.Set("PRODUCT_STATE", state.ToString().ToUpperInvariant()).Set("JUDGEMENT", result.ToString().ToUpperInvariant()).Set("STARTED_AT", startedAt?.ToString("O", System.Globalization.CultureInfo.InvariantCulture) ?? "").Set("COMPLETED_AT", completedAt?.ToString("O", System.Globalization.CultureInfo.InvariantCulture) ?? "");
    }

    private static readonly IReadOnlySet<string> HostWritableProductFieldNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "H_GLASSID",
        "E_GLASSID",
        "LOTID",
        "BATCHID",
        "JOBID",
        "PORTID",
        "SLOTNO",
        "PRODUCT_TYPE",
        "PRODUCT_KIND",
        "PRODUCTID",
        "RUNSPECID",
        "LAYERID",
        "STEPID",
        "FLOWID",
        "GLASS_SIZE",
        "GLASS_THICKNESS",
        "GLASS_STATE",
        "GLASS_ORDER",
        "COMMENT",
        "USE_COUNT",
        "REASON_CODE",
        "INS_FLAG",
        "ENC_FLAG",
        "PRERUN_FLAG",
        "TURN_DIR",
        "FLIP_STATE",
        "WORK_STATE",
        "MULTI_USE",
        "PAIR_GLASSID",
        "PAIR_PPID"
    };
    public static readonly IReadOnlyList<string> ProductFieldNames = [
        "H_GLASSID", 
        "E_GLASSID", 
        "LOTID", 
        "BATCHID", 
        "JOBID", 
        "PORTID", 
        "SLOTNO", 
        "PRODUCT_TYPE", 
        "PRODUCT_KIND", 
        "PRODUCTID", 
        "RUNSPECID", 
        "LAYERID", 
        "STEPID", 
        "PROCESS_ID", 
        "PPID", 
        "FLOWID", 
        "GLASS_SIZE", 
        "GLASS_THICKNESS", 
        "GLASS_STATE", 
        "GLASS_ORDER", 
        "COMMENT", 
        "USE_COUNT", 
        "PRODUCT_STATE", 
        "JUDGEMENT", 
        "REASON_CODE", 
        "INS_FLAG", 
        "ENC_FLAG", 
        "PRERUN_FLAG", 
        "TURN_DIR", 
        "FLIP_STATE", 
        "WORK_STATE", 
        "MULTI_USE", 
        "PAIR_GLASSID", 
        "PAIR_PPID", 
        "CREATED_AT", 
        "STARTED_AT", 
        "COMPLETED_AT", 
        "PROCESS_STEP"
    ];
    private static string ChooseGlassId(string processId, string glassId, IReadOnlyDictionary<string, string> parameters)
    {
        if (!string.IsNullOrWhiteSpace(glassId))
        {
            return glassId.Trim();
        }

        foreach (var key in new[]
        {
            "HGlassId",
            "H_GLASSID",
            "PanelId",
            "PANEL_ID",
            "PanelID"
        }

        )
        {
            if (parameters.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return string.IsNullOrWhiteSpace(processId) ? $"GLASS_{DateTime.Now:yyyyMMddHHmmssfff}" : processId.Trim().Replace('-', '_');
    }
}

public enum EN_PRODUCT_STATE
{
    Created,
    Running,
    Completed,
    Stopped,
    Error,
    Scrapped
}

public enum EN_PRODUCT_RESULT
{
    Pending,
    Skipped,
    OK,
    NG
}

public enum EN_PRODUCT_HEAD_STATE
{
    Ready,
    Running,
    Completed,
    Stopped,
    Error,
    Disabled
}

public enum EN_PRODUCT_PROCESS_STATE
{
    NotStarted,
    Running,
    Completed,
    Stopped,
    Error
}

public sealed record ST_PRODUCT_ALIGN_DATA(EN_PRODUCT_PROCESS_STATE State, string Result, double? OffsetX, double? OffsetY, double? Rotation);
public sealed record ST_PRODUCT_DISTORTION_DATA(EN_PRODUCT_PROCESS_STATE State, string Result, double? MaxDa);
public sealed record ST_PRODUCT_REVIEW_RESULT(string ShotKey, int HeadNo, int CellNo, int BeamNo, double ErrorX, double ErrorY, string Judge);
public sealed record ST_PRODUCT_REVIEW_DATA(EN_PRODUCT_PROCESS_STATE State, DateTimeOffset? SavedAt, IReadOnlyList<ST_PRODUCT_REVIEW_RESULT> Results)
{
    public string InspectionMethod { get; init; } = "";
    public string RecipeId { get; init; } = "";
    public string Rule { get; init; } = "";
    public string RuleScope { get; init; } = "";
    public int BeamNo { get; init; } = 1;
    public int? ReferenceCellNo { get; init; }
    public string ReferenceShot { get; init; } = "";
    public string RoughReference { get; init; } = "";
    public int FineLineCount { get; init; }
    public int TotalCount { get; init; }
}

public sealed record ST_PRODUCT_INFO(IReadOnlyDictionary<string, string> Values)
{
    public string Get(string name)
    {
        return Values.TryGetValue(name, out var value) ? value : "";
    }

    public ST_PRODUCT_INFO Set(string name, string value)
    {
        var values = new Dictionary<string, string>(Values, StringComparer.OrdinalIgnoreCase)
        {
            [name] = value
        };
        return new ST_PRODUCT_INFO(values);
    }
}

public sealed record ST_PRODUCT_RECIPE_DATA(IReadOnlyDictionary<string, string> Parameters);
public sealed record ST_PRODUCT_RUNTIME_DATA(IReadOnlyList<ST_PRODUCT_HEAD_RESULT> Heads);
public sealed record ST_PRODUCT_HEAD_RESULT(
    int HeadNo, 
    EN_PRODUCT_HEAD_STATE State, 
    int TotalPoints, 
    int CompletedPoints, 
    EN_PRODUCT_RESULT Result, 
    string ErrorCode, 
    string Message, 
    DateTimeOffset? StartedAt, 
    DateTimeOffset? CompletedAt
);
public sealed record ST_PRODUCT_DATA(
    ST_PRODUCT_INFO Product, 
    ST_PRODUCT_RECIPE_DATA Recipe, 
    ST_PRODUCT_ALIGN_DATA Align, 
    ST_PRODUCT_DISTORTION_DATA Distortion, 
    ST_PRODUCT_REVIEW_DATA Review, 
    ST_PRODUCT_RUNTIME_DATA Runtime
)
{
    public string GlassId => Product.Get("H_GLASSID");

    public string ProductId
    {
        get
        {
            return Product.Get("PRODUCTID");
        }
    }

    public string PanelId
    {
        get
        {
            return Product.Get("H_GLASSID");
        }
    }

    public string LotId
    {
        get
        {
            return Product.Get("LOTID");
        }
    }

    public string ProcessId
    {
        get
        {
            return Product.Get("PROCESS_ID");
        }
    }

    public string RecipeId
    {
        get
        {
            return Product.Get("PPID");
        }
    }

    public EN_PRODUCT_STATE State
    {
        get
        {
            return ParseEnum(Product.Get("PRODUCT_STATE"), EN_PRODUCT_STATE.Created);
        }
    }

    public EN_PRODUCT_RESULT Result
    {
        get
        {
            return ParseEnum(Product.Get("JUDGEMENT"), EN_PRODUCT_RESULT.Pending);
        }
    }

    public DateTimeOffset? CreatedAt
    {
        get
        {
            return ParseDate(Product.Get("CREATED_AT"));
        }
    }

    public DateTimeOffset? StartedAt
    {
        get
        {
            return ParseDate(Product.Get("STARTED_AT"));
        }
    }

    public DateTimeOffset? CompletedAt
    {
        get
        {
            return ParseDate(Product.Get("COMPLETED_AT"));
        }
    }

    public IReadOnlyDictionary<string, string> Parameters
    {
        get
        {
            return Recipe.Parameters;
        }
    }

    public IReadOnlyList<ST_PRODUCT_HEAD_RESULT> Heads
    {
        get
        {
            return Runtime.Heads;
        }
    }

    private static T ParseEnum<T>(string value, T defaultValue)
        where T : struct, Enum
    {
        return Enum.TryParse<T>(value, true, out var parsed) ? parsed : defaultValue;
    }

    private static DateTimeOffset? ParseDate(string value)
    {
        return DateTimeOffset.TryParse(value, out var parsed) ? parsed : null;
    }
}

public sealed record ST_PRODUCT_HISTORY(DateTimeOffset OccurredAt, string GlassId, string ProcessId, string RecipeId, string Action, string State, string Result, string Detail);
public abstract class CProductFileBase
{
    public abstract Task<ST_PRODUCT_DATA?> LoadActive(CancellationToken cancellationToken = default);
    public abstract Task SaveActive(ST_PRODUCT_DATA product, CancellationToken cancellationToken = default);
    public abstract Task ClearActive(CancellationToken cancellationToken = default);
    public abstract Task AppendHistory(ST_PRODUCT_HISTORY history, CancellationToken cancellationToken = default);
    public abstract Task<IReadOnlyList<ST_PRODUCT_HISTORY>> LoadHistory(int maxRows = 100, int days = 14, CancellationToken cancellationToken = default);
}
