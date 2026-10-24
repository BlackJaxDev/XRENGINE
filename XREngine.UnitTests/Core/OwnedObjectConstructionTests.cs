using NUnit.Framework;
using XREngine.Core.Files;
using XREngine.Data.Core;

namespace XREngine.UnitTests.Core;

[TestFixture, NonParallelizable]
public sealed class OwnedObjectConstructionTests
{
    [Test]
    public void FinalValidationFailureRollsBackPublishedEntriesAndRetainsOriginalFailure()
    {
        InvalidOperationException failure = new("Expected final validation failure.");
        bool sawPublished = false;
        ObjectConstructionResult<OwnedConstructionProbe> result = XRObjectBase.ConstructOwned(
            _ => new OwnedConstructionProbe(), value =>
            {
                sawPublished = ReferenceEquals(XRObjectBase.ObjectsCache[value.ID], value);
                throw failure;
            });
        try
        {
            Assert.That(sawPublished, Is.True);
            Assert.That(result.Failure, Is.SameAs(failure));
            Assert.That(result.Value!.IsDestroyed, Is.False);
            Assert.That(result.Value.DestroyCount, Is.Zero);
            Assert.That(result.Ownership.Contains(result.Value), Is.True);
            Assert.That(XRObjectBase.ObjectsCache.ContainsKey(result.Value.ID), Is.False);
        }
        finally { RetireProbes(result.Ownership); }
    }

    [Test]
    public void FinalValidationRunsOutsideTheCacheLock()
    {
        OwnedConstructionProbe ordinary = new();
        Task? worker = null;
        ObjectConstructionResult<OwnedConstructionProbe> result = XRObjectBase.ConstructOwned(
            _ => new OwnedConstructionProbe(), _ =>
            {
                worker = Task.Run(ordinary.RequestPublication);
                if (!worker.Wait(TimeSpan.FromSeconds(5)))
                    throw new TimeoutException("Final validation held the cache mutation lock.");
            });
        try
        {
            worker!.GetAwaiter().GetResult();
            Assert.That(result.Failure, Is.Null);
        }
        finally
        {
            worker?.GetAwaiter().GetResult();
            RetireProbes(result.Ownership);
            ordinary.Destroy(true);
        }
    }

    [Test]
    public void FinalValidationCannotLeaveANestedPublicationScopeOpen()
    {
        ObjectConstructionResult<OwnedConstructionProbe> result = XRObjectBase.ConstructOwned(
            _ => new OwnedConstructionProbe(), _ => XRObjectBase.BeginDeferredObjectCachePublication());
        try
        {
            Assert.That(result.Failure, Is.Not.Null);
            Assert.That(result.Value!.IsDestroyed, Is.False);
            Assert.That(XRObjectBase.ObjectsCache.ContainsKey(result.Value.ID), Is.False);
        }
        finally { RetireProbes(result.Ownership); }
    }

    [Test]
    public void ConstructorOriginIncludesSuppressedAndNonCacheAllocations()
    {
        TextFile? suppressed = null;
        NonCacheConstructionProbe? nonCache = null;
        ObjectConstructionResult<OwnedConstructionProbe> result = XRObjectBase.ConstructOwned(context =>
        {
            using (XRObjectBase.SuppressObjectCacheRegistration())
                suppressed = new TextFile();
            nonCache = new();
            OwnedConstructionProbe value = new();
            value.RequestPublication();
            Assert.That(XRObjectBase.ObjectsCache.ContainsKey(value.ID), Is.False);
            context.RequireAllocation(value);
            context.RequireAllocation(suppressed);
            context.RequireAllocation(nonCache);
            return value;
        });
        try
        {
            Assert.That(result.Succeeded, Is.True);
            Assert.That(result.Ownership.Contains(suppressed!), Is.True);
            Assert.That(result.Ownership.Contains(nonCache!), Is.True);
            Assert.That(XRObjectBase.ObjectsCache.ContainsKey(suppressed!.ID), Is.False);
            Assert.That(XRObjectBase.ObjectsCache.ContainsKey(nonCache!.ID), Is.False);
            Assert.That(XRObjectBase.ObjectsCache[result.Value!.ID], Is.SameAs(result.Value));
        }
        finally { RetireProbes(result.Ownership); }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void IndependentNestedFactoriesKeepTheirOwnConstructorOrigins(bool ownedNestedFactory)
    {
        OwnedConstructionProbe? nested = null;
        OwnedConstructionProbe? sharedBatch = null;
        ObjectConstructionResult<OwnedConstructionProbe> result = XRObjectBase.ConstructOwned(context =>
        {
            OwnedConstructionProbe value = new();
            using (ObjectCachePublicationScope shared = XRObjectBase.BeginDeferredObjectCachePublication())
            {
                sharedBatch = new();
                shared.Complete();
            }
            if (ownedNestedFactory)
            {
                ObjectConstructionResult<OwnedConstructionProbe> separate = XRObjectBase.ConstructOwned(_ => new OwnedConstructionProbe());
                Assert.That(separate.Succeeded, Is.True);
                nested = separate.Value!;
                Assert.That(separate.Ownership.Contains(nested), Is.True);
            }
            else
            {
                using ObjectCachePublicationScope separate = XRObjectBase.BeginIndependentObjectCachePublication();
                nested = new();
                separate.Complete();
            }
            Assert.That(context.OwnsAllocation(sharedBatch), Is.True);
            Assert.That(context.OwnsAllocation(nested), Is.False);
            return value;
        });
        try
        {
            Assert.That(result.Succeeded, Is.True);
            Assert.That(result.Ownership.Contains(sharedBatch!), Is.True);
            Assert.That(result.Ownership.Contains(nested!), Is.False);
            RetireProbes(result.Ownership);
            Assert.That(nested!.IsDestroyed, Is.False);
            Assert.That(XRObjectBase.ObjectsCache[nested.ID], Is.SameAs(nested));
        }
        finally { RetireProbes(result.Ownership); nested?.Destroy(true); }
    }

    [Test]
    public void BorrowedGenerateIsRejectedBeforeIdentityOrCacheChanges()
    {
        TextFile borrowed = new();
        Guid originalId = borrowed.ID;
        ObjectConstructionResult<TextFile> result = XRObjectBase.ConstructOwned(context =>
        {
            _ = new OwnedConstructionProbe();
            borrowed.Generate();
            return borrowed;
        });
        try
        {
            Assert.That(result.Failure, Is.TypeOf<InvalidOperationException>());
            Assert.That(result.Ownership.Contains(borrowed), Is.False);
            Assert.That(borrowed.ID, Is.EqualTo(originalId));
            Assert.That(borrowed.IsDestroyed, Is.False);
            Assert.That(XRObjectBase.ObjectsCache[originalId], Is.SameAs(borrowed));
            Assert.That(result.Ownership.Objects, Has.Count.EqualTo(1));
            Assert.That(result.Ownership.Objects[0].IsDestroyed, Is.False);
        }
        finally { RetireProbes(result.Ownership); borrowed.Destroy(true); }
    }

    [Test]
    public void AThrowingDerivedConstructorRemainsInTheFailureLedger()
    {
        ObjectConstructionResult<OwnedConstructionProbe> result = XRObjectBase.ConstructOwned(
            _ => new OwnedConstructionProbe(failConstruction: true));
        try
        {
            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Value, Is.Null);
            Assert.That(result.Failure!.Message, Is.EqualTo("Expected constructor failure."));
            OwnedConstructionProbe partial = (OwnedConstructionProbe)result.Ownership.Objects.Single();
            Assert.That(partial.IsDestroyed, Is.False);
            Assert.That(partial.DestroyCount, Is.Zero);
            Assert.That(XRObjectBase.ObjectsCache.ContainsKey(partial.ID), Is.False);
            TextFile later = new();
            try { Assert.That(XRObjectBase.ObjectsCache[later.ID], Is.SameAs(later)); }
            finally { later.Destroy(true); }
        }
        finally { RetireProbes(result.Ownership); }
    }

    [Test]
    public void IncompleteNestedScopeRetainsTheSharedBatchWithoutDestruction()
    {
        ObjectConstructionResult<OwnedConstructionProbe> result = XRObjectBase.ConstructOwned(context =>
        {
            OwnedConstructionProbe value = new();
            using (XRObjectBase.BeginDeferredObjectCachePublication())
                _ = new OwnedConstructionProbe();
            return value;
        });
        try
        {
            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Ownership.Objects, Has.Count.EqualTo(2));
            foreach (XRObjectBase value in result.Ownership.Objects)
            {
                Assert.That(value.IsDestroyed, Is.False);
                Assert.That(XRObjectBase.ObjectsCache.ContainsKey(value.ID), Is.False);
            }
        }
        finally { RetireProbes(result.Ownership); }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void PublicationFailureRemovesOnlyTheExactEntriesInsertedByThisBatch(bool equalReplacement)
    {
        XRObjectBase borrowed = equalReplacement ? new OwnedConstructionProbe() : new TextFile();
        OwnedConstructionProbe? first = null;
        InvalidOperationException expected = new("Expected publication failure.");
        ObjectConstructionResult<OwnedConstructionProbe> result = XRObjectBase.ConstructOwned(_ =>
        {
            first = new();
            OwnedConstructionProbe second = new();
            second.Publishing = () =>
            {
                first.Destroy(true);
                borrowed.AdoptPersistentID(first.ID);
                throw expected;
            };
            return first;
        });
        try
        {
            Assert.That(result.Failure, Is.SameAs(expected));
            Assert.That(result.Value, Is.SameAs(first));
            Assert.That(result.Ownership.Contains(borrowed), Is.False);
            Assert.That(XRObjectBase.ObjectsCache[first!.ID], Is.SameAs(borrowed));
            Assert.That(borrowed.IsDestroyed, Is.False);
            Assert.That(first.EqualityCallCount, Is.Zero);
            if (borrowed is OwnedConstructionProbe replacement)
                Assert.That(replacement.EqualityCallCount, Is.Zero);
            OwnedConstructionProbe second = result.Ownership.Objects.OfType<OwnedConstructionProbe>().Last();
            Assert.That(second.IsDestroyed, Is.False);
            Assert.That(XRObjectBase.ObjectsCache.ContainsKey(second.ID), Is.False);
        }
        finally { RetireProbes(result.Ownership); borrowed.Destroy(true); }
    }

    [Test]
    public void PersistentNestedIdentityKeepsItsIncumbentCacheOwner()
    {
        TextFile incumbent = new();
        ObjectConstructionResult<TextFile> result = XRObjectBase.ConstructOwned(_ =>
        {
            TextFile clone = new();
            clone.AdoptPersistentID(incumbent.ID);
            return clone;
        });
        try
        {
            Assert.That(result.Succeeded, Is.True);
            Assert.That(result.Value!.ID, Is.EqualTo(incumbent.ID));
            Assert.That(result.Ownership.Contains(incumbent), Is.False);
            Assert.That(XRObjectBase.ObjectsCache[incumbent.ID], Is.SameAs(incumbent));
            RetireProbes(result.Ownership);
            Assert.That(XRObjectBase.ObjectsCache[incumbent.ID], Is.SameAs(incumbent));
        }
        finally { RetireProbes(result.Ownership); incumbent.Destroy(true); }
    }

    [TestCase(false, false)]
    [TestCase(true, false)]
    [TestCase(false, true)]
    [TestCase(true, true)]
    public void PublicationCallbacksCannotMoveAnOwnedIdentity(bool regenerate, bool independentCallbackScope)
    {
        OwnedConstructionProbe? first = null;
        Guid originalId = Guid.Empty;
        Guid requestedId = Guid.NewGuid();
        ObjectConstructionResult<OwnedConstructionProbe> result = XRObjectBase.ConstructOwned(context =>
        {
            first = new();
            originalId = first.ID;
            OwnedConstructionProbe second = new();
            second.Publishing = () =>
            {
                using ObjectCachePublicationScope? nested = independentCallbackScope
                    ? XRObjectBase.BeginIndependentObjectCachePublication() : null;
                if (regenerate)
                    first.Generate();
                else
                    first.AdoptPersistentID(requestedId);
                nested?.Complete();
            };
            return first;
        });
        try
        {
            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Failure, Is.TypeOf<InvalidOperationException>());
            Assert.That(first!.ID, Is.EqualTo(originalId));
            Assert.That(XRObjectBase.ObjectsCache.ContainsKey(originalId), Is.False);
            Assert.That(XRObjectBase.ObjectsCache.ContainsKey(requestedId), Is.False);
            foreach (XRObjectBase value in result.Ownership.Objects)
            {
                Assert.That(value.IsDestroyed, Is.False);
                Assert.That(XRObjectBase.ObjectsCache.ContainsKey(value.ID), Is.False);
            }
        }
        finally { RetireProbes(result.Ownership); }
    }

    [Test]
    public void PublicationCannotReportSuccessWithAnUnclosedNestedScope()
    {
        ObjectCachePublicationScope? unclosed = null;
        ObjectConstructionResult<OwnedConstructionProbe> result = XRObjectBase.ConstructOwned(context =>
        {
            OwnedConstructionProbe value = new();
            value.Publishing = () => unclosed = XRObjectBase.BeginDeferredObjectCachePublication();
            return value;
        });
        try
        {
            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Failure, Is.Not.Null);
            Assert.That(result.Value!.IsDestroyed, Is.False);
            Assert.That(XRObjectBase.ObjectsCache.ContainsKey(result.Value.ID), Is.False);
            Assert.Throws<InvalidOperationException>(() => unclosed!.Dispose());
            TextFile later = new();
            try { Assert.That(XRObjectBase.ObjectsCache[later.ID], Is.SameAs(later)); }
            finally { later.Destroy(true); }
        }
        finally { RetireProbes(result.Ownership); }
    }

    [TestCase(false, 0)]
    [TestCase(false, 1)]
    [TestCase(false, 2)]
    [TestCase(true, 0)]
    [TestCase(true, 1)]
    [TestCase(true, 2)]
    public void ForeignThreadsCannotChangeAnOwnedIdentityBeforeTheResultIsSettled(bool duringPublication, int operation)
    {
        Task<Exception?>? attempt = null;
        ObjectConstructionResult<OwnedConstructionProbe>? result = null;
        OwnedConstructionProbe? first = null;
        Guid originalId = Guid.Empty;
        Guid requestedId = Guid.NewGuid();
        bool finishedBeforeFactoryExit = false;
        InvalidOperationException expected = new("Expected failure after the worker attempt.");
        void AttemptMutation()
        {
            attempt = Task.Run<Exception?>(() =>
            {
                try
                {
                    switch (operation)
                    {
                        case 0: first!.AdoptPersistentID(requestedId); break;
                        case 1: first!.Generate(); break;
                        case 2: first!.RequestPublication(); break;
                    }
                    return null;
                }
                catch (Exception error) { return error; }
            });
            // A broken guard can block behind the publisher's cache lock. Release
            // that lock through the factory failure, then drain the worker below.
            finishedBeforeFactoryExit = attempt.Wait(TimeSpan.FromSeconds(5));
            throw expected;
        }
        try
        {
            result = XRObjectBase.ConstructOwned(context =>
            {
                first = new();
                originalId = first.ID;
                if (duringPublication)
                {
                    OwnedConstructionProbe second = new();
                    second.Publishing = AttemptMutation;
                }
                else
                    AttemptMutation();
                return first;
            });
            Assert.That(attempt!.Wait(TimeSpan.FromSeconds(5)), Is.True);
            Assert.That(finishedBeforeFactoryExit, Is.True, "Identity admission must reject before the cache lock.");
            Assert.That(attempt.Result, Is.TypeOf<InvalidOperationException>());
            Assert.That(result.Failure, Is.SameAs(expected));
            Assert.That(first!.ID, Is.EqualTo(originalId));
            Assert.That(XRObjectBase.ObjectsCache.ContainsKey(originalId), Is.False);
            Assert.That(XRObjectBase.ObjectsCache.ContainsKey(requestedId), Is.False);
            Assert.That(first.IsDestroyed, Is.False);

            first.AdoptPersistentID(requestedId);
            first.RequestPublication();
            Assert.That(XRObjectBase.ObjectsCache[requestedId], Is.SameAs(first), "The settled result must release its temporary identity guard.");
        }
        finally
        {
            // ConstructOwned has released the cache lock even when an assertion fails.
            // No worker can remain to change an object after fixture cleanup.
            attempt?.GetAwaiter().GetResult();
            if (result is not null)
                RetireProbes(result.Ownership);
        }
    }

    [Test]
    public void ConstructionAttemptDuringPublicationRetainsItsPartialAllocation()
    {
        ObjectConstructionResult<OwnedConstructionProbe> result = XRObjectBase.ConstructOwned(context =>
        {
            OwnedConstructionProbe value = new();
            value.Publishing = () => _ = new OwnedConstructionProbe();
            return value;
        });
        try
        {
            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Failure, Is.TypeOf<InvalidOperationException>());
            Assert.That(result.Ownership.Objects, Has.Count.EqualTo(2));
            foreach (XRObjectBase value in result.Ownership.Objects)
            {
                Assert.That(value.IsDestroyed, Is.False);
                Assert.That(XRObjectBase.ObjectsCache.ContainsKey(value.ID), Is.False);
            }
        }
        finally { RetireProbes(result.Ownership); }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void AnIndependentLegacyScopeCannotEnrollOrPublishAnActiveOwnedObject(bool requestPublication)
    {
        OwnedConstructionProbe? value = null;
        Guid originalId = Guid.Empty;
        ObjectConstructionResult<OwnedConstructionProbe> result = XRObjectBase.ConstructOwned<OwnedConstructionProbe>(context =>
        {
            value = new();
            originalId = value.ID;
            using (XRObjectBase.BeginIndependentObjectCachePublication())
            {
                if (requestPublication)
                    value.RequestPublication();
                else
                    value.Generate();
            }
            throw new InvalidOperationException("Expected outer factory failure.");
        });
        try
        {
            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Failure, Is.TypeOf<InvalidOperationException>());
            Assert.That(value!.ID, Is.EqualTo(originalId));
            Assert.That(value.IsDestroyed, Is.False, "The inner legacy scope must not own or abort the outer allocation.");
            Assert.That(value.DestroyCount, Is.Zero);
            Assert.That(result.Ownership.Contains(value), Is.True);
            Assert.That(XRObjectBase.ObjectsCache.ContainsKey(originalId), Is.False);
        }
        finally { RetireProbes(result.Ownership); }
    }

    private static void RetireProbes(ObjectConstructionOwnership ownership)
    {
        // These test objects have no graph edges or owner-changing lifecycle callbacks.
        foreach (XRObjectBase value in ownership.Objects)
            value.Destroy(true);
    }
}
