#if GC2_INVENTORY
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using Arawn.GameCreator2.Networking;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.Inventory;
using GameCreator.Runtime.VisualScripting;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Inventory
{
    /// <summary>
    /// Small, transport-neutral runtime panel used by the generated PurrNet Inventory regression
    /// scene. It deliberately exercises the public semantic request paths instead of mutating a
    /// Bag directly, so the same checks can be reused by another transport.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("")]
    public sealed class NetworkInventoryAuthorityRegressionHarness : MonoBehaviour
    {
        private const int ValidatedSourceHash = unchecked((int)0x4E495633); // "NIV3"
        private const int RejectedSourceHash = unchecked((int)0x52454A33);  // "REJ3"
        private const float OperationTimeout = 6f;

        [SerializeField] private Item m_TestItem;
        [SerializeField] private NetworkInventoryPickupSource m_StaticPickup;
        [SerializeField] private Trigger m_ValidatedAddTrigger;
        [SerializeField] private NetworkInventoryRegressionContinuationMarker m_ValidatedAddMarker;
        [SerializeField] private Trigger m_StaticPickupTrigger;
        [SerializeField] private NetworkInventoryRegressionContinuationMarker m_StaticPickupMarker;
        [SerializeField] private bool m_ShowPanel = true;
        [SerializeField] private KeyCode m_ToggleKey = KeyCode.F8;

        private readonly List<string> m_Results = new(12);
        private NetworkInventoryController m_LocalController;
        private NetworkInventoryManager m_ValidatorManager;
        private Func<NetworkContentAddRequest, uint,
            (bool allowed, InventoryRejectionReason reason)> m_PreviousAddValidator;
        private Func<NetworkContentAddRequest, uint,
            (bool allowed, InventoryRejectionReason reason)> m_HarnessAddValidator;
        private bool m_Busy;
        private Vector2 m_Scroll;
        private ushort m_ResyncRequestId;

        private static readonly PropertyInfo s_SelectedItemProperty = typeof(RuntimeItem).GetProperty(
            nameof(RuntimeItem.UI_LastItemSelected),
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);

        public Item TestItem => m_TestItem;
        public NetworkInventoryPickupSource StaticPickup => m_StaticPickup;

        private void Awake()
        {
            m_HarnessAddValidator = ValidateHarnessAdd;
            AddResult("READY", "Start a Host and a connected Client, then run the checks on the Client.");
        }

        private void Update()
        {
            ResolveLocalController();
            EnsureValidatorInstalled();
        }

        private void OnDisable()
        {
            RestoreValidator();
        }

        private void EnsureValidatorInstalled()
        {
            NetworkInventoryManager manager = NetworkInventoryManager.Instance;
            if (manager == m_ValidatorManager) return;

            RestoreValidator();
            if (manager == null) return;

            m_ValidatorManager = manager;
            m_PreviousAddValidator = manager.CustomAddValidator;
            manager.CustomAddValidator = m_HarnessAddValidator;
        }

        private void RestoreValidator()
        {
            if (m_ValidatorManager != null &&
                m_ValidatorManager.CustomAddValidator == m_HarnessAddValidator)
            {
                m_ValidatorManager.CustomAddValidator = m_PreviousAddValidator;
            }

            m_ValidatorManager = null;
            m_PreviousAddValidator = null;
        }

        private (bool allowed, InventoryRejectionReason reason) ValidateHarnessAdd(
            NetworkContentAddRequest request,
            uint senderClientId)
        {
            if (m_TestItem != null &&
                request.Source == InventoryModificationSource.Quest &&
                request.SourceHash == ValidatedSourceHash &&
                request.ItemHash == m_TestItem.ID.Hash &&
                request.ItemIdString == m_TestItem.ID.String)
            {
                return (true, InventoryRejectionReason.None);
            }

            // The stock GC2 Add Item instruction has no source hash. This exception exists only
            // in the generated regression scene and lets the harness exercise the real patched
            // instruction/continuation path rather than bypassing it through a public request API.
            if (m_TestItem != null &&
                request.Source == InventoryModificationSource.Direct &&
                request.SourceHash == 0 &&
                request.ItemHash == m_TestItem.ID.Hash &&
                request.ItemIdString == m_TestItem.ID.String)
            {
                return (true, InventoryRejectionReason.None);
            }

            if (request.Source == InventoryModificationSource.Direct &&
                request.SourceHash == RejectedSourceHash)
            {
                return (false, InventoryRejectionReason.NotAuthorized);
            }

            if (m_PreviousAddValidator != null)
                return m_PreviousAddValidator.Invoke(request, senderClientId);

            return (false, InventoryRejectionReason.NotAuthorized);
        }

        private bool ResolveLocalController()
        {
            if (m_LocalController != null && m_LocalController.isActiveAndEnabled &&
                m_LocalController.IsLocalClient && !m_LocalController.IsWorldInventory)
            {
                return true;
            }

            m_LocalController = null;
            NetworkInventoryController[] controllers = FindObjectsByType<NetworkInventoryController>(
                FindObjectsInactive.Exclude,
                FindObjectsSortMode.None);

            for (int i = 0; i < controllers.Length; i++)
            {
                NetworkInventoryController candidate = controllers[i];
                if (candidate == null || !candidate.IsLocalClient || candidate.IsWorldInventory) continue;
                if (candidate.NetworkId == 0) continue;
                m_LocalController = candidate;
                break;
            }

            return m_LocalController != null;
        }

        private void OnGUI()
        {
            UnityEngine.Event guiEvent = UnityEngine.Event.current;
            if (guiEvent != null && guiEvent.type == EventType.KeyDown &&
                guiEvent.keyCode == m_ToggleKey)
            {
                m_ShowPanel = !m_ShowPanel;
                guiEvent.Use();
            }

            if (!m_ShowPanel) return;

            const float width = 500f;
            float left = Mathf.Max(10f, Screen.width - width - 18f);
            GUILayout.BeginArea(new Rect(left, 18f, width, Mathf.Min(Screen.height - 36f, 650f)),
                GUI.skin.window);

            GUILayout.Label("Inventory Authority v3 Customer Regression", GUI.skin.box);
            GUILayout.Label($"Toggle panel: {m_ToggleKey}");
            GUILayout.Label(RoleDescription());
            GUILayout.Label(m_TestItem != null
                ? $"Test item: {m_TestItem.name} ({m_TestItem.ID.String})"
                : "Test item: MISSING");
            GUILayout.Label(
                "Customer acceptance: run on a connected client. Validated Add must survive later " +
                "sync; rejected Add must never appear; pickup grants once; organization persists; " +
                "snapshot keeps the selected RuntimeItem.",
                GUI.skin.textArea);

            GUI.enabled = !m_Busy && ResolveLocalController() && m_TestItem != null;
            if (GUILayout.Button("1. Patched Add Item instruction (must continue + persist)"))
                _ = RunGuardedAsync(TestValidatedAddAsync);
            if (GUILayout.Button("2. Unvalidated client Add (must be rejected)"))
                _ = RunGuardedAsync(TestRejectedAddAsync);
            if (GUILayout.Button("3. Static pickup twice (exactly one success)"))
                _ = RunGuardedAsync(TestStaticPickupAsync);
            if (GUILayout.Button("4. Native stack, Split, then Move/merge"))
                _ = RunGuardedAsync(TestStackSplitMoveAsync);
            if (GUILayout.Button("5. Force applied snapshot; preserve reference + selection"))
                _ = RunGuardedAsync(TestReferenceStabilityAsync);
            if (GUILayout.Button("Run all available checks"))
                _ = RunGuardedAsync(RunAllAsync);
            GUI.enabled = true;

            if (m_Busy) GUILayout.Label("Running…", GUI.skin.box);
            GUILayout.Space(4f);
            m_Scroll = GUILayout.BeginScrollView(m_Scroll, GUILayout.ExpandHeight(true));
            for (int i = m_Results.Count - 1; i >= 0; i--) GUILayout.Label(m_Results[i]);
            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }

        private string RoleDescription()
        {
            if (!ResolveLocalController()) return "Role: waiting for the local Inventory controller";
            string role = m_LocalController.IsServer ? "Host/server owner" : "Connected client owner";
            return $"Role: {role}, actor={m_LocalController.NetworkId}";
        }

        private async Task RunGuardedAsync(Func<Task> test)
        {
            if (m_Busy) return;
            m_Busy = true;
            try
            {
                await test.Invoke();
            }
            catch (Exception exception)
            {
                AddResult("FAIL", $"Unhandled test exception: {exception.GetType().Name}: {exception.Message}");
                Debug.LogException(exception, this);
            }
            finally
            {
                m_Busy = false;
            }
        }

        private async Task RunAllAsync()
        {
            await TestValidatedAddAsync();
            await TestRejectedAddAsync();
            await TestStackSplitMoveAsync();
            await TestReferenceStabilityAsync();
            await TestStaticPickupAsync();
        }

        private async Task TestValidatedAddAsync()
        {
            if (!RequireController(out NetworkInventoryController controller)) return;
            if (controller.IsServer)
            {
                AddResult("SKIP", "Validated client Add must be run on a connected client; host grants are trusted server operations.");
                return;
            }

            if (m_ValidatedAddTrigger == null || m_ValidatedAddMarker == null)
            {
                AddResult("FAIL", "The generated Add Item instruction fixture is missing.");
                return;
            }

            int before = CountTestItems(controller);
            int continuationsBefore = m_ValidatedAddMarker.CompletedCount;
            await m_ValidatedAddTrigger.Execute(new Args(
                m_ValidatedAddTrigger.gameObject,
                controller.gameObject));

            bool converged = await WaitUntilAsync(() => CountTestItems(controller) == before + 1);
            bool snapshotApplied = converged && await ForceRecoverySnapshotAsync(controller);
            bool persisted = snapshotApplied &&
                await CountRemainsAsync(controller, before + 1, 1.25f);
            bool continued = m_ValidatedAddMarker.CompletedCount == continuationsBefore + 1;
            bool pass = converged && snapshotApplied && persisted && continued;
            AddResult(pass ? "PASS" : "FAIL",
                $"Patched Add Item instruction: continued={continued}, count " +
                $"{before}->{CountTestItems(controller)}, recoverySnapshot={snapshotApplied}, " +
                $"persistedAcrossSync={persisted}");
        }

        private async Task TestRejectedAddAsync()
        {
            if (!RequireController(out NetworkInventoryController controller)) return;
            if (controller.IsServer)
            {
                AddResult("SKIP", "Rejected-Add is a connected-client security check; the host is trusted server code.");
                return;
            }

            int before = CountTestItems(controller);
            Task<NetworkContentAddResponse> request = controller.RequestAddItemAsync(
                m_TestItem,
                TBagContent.INVALID,
                true,
                InventoryModificationSource.Direct,
                RejectedSourceHash);

            bool remainedAbsent = await CountRemainsAsync(controller, before, 1.25f);
            NetworkContentAddResponse response = await request;
            int after = CountTestItems(controller);
            bool pass = !response.Authorized && remainedAbsent && before == after;
            AddResult(pass ? "PASS" : "FAIL",
                $"Unvalidated Add: authorized={response.Authorized}, reason={response.RejectionReason}, " +
                $"count {before}->{after}, neverAppeared={remainedAbsent}");
        }

        private async Task TestStaticPickupAsync()
        {
            if (!RequireController(out NetworkInventoryController controller)) return;
            if (m_StaticPickup == null)
            {
                AddResult("FAIL", "Generated static pickup fixture is missing.");
                return;
            }

            if (m_StaticPickupTrigger == null || m_StaticPickupMarker == null)
            {
                AddResult("FAIL", "The converted stock pickup Trigger fixture is missing.");
                return;
            }

            int before = CountTestItems(controller);
            bool alreadyConsumed = m_StaticPickup.IsConsumed;
            int continuationsBefore = m_StaticPickupMarker.CompletedCount;

            await m_StaticPickupTrigger.Execute(new Args(
                m_StaticPickupTrigger.gameObject,
                controller.gameObject));
            await Task.Delay(200);
            await m_StaticPickupTrigger.Execute(new Args(
                m_StaticPickupTrigger.gameObject,
                controller.gameObject));
            await Task.Delay(200);

            int after = CountTestItems(controller);
            int successes = m_StaticPickupMarker.CompletedCount - continuationsBefore;
            bool wonRace = !alreadyConsumed && successes == 1 && after == before + 1 &&
                           m_StaticPickup.IsConsumed;
            bool lostRace = !alreadyConsumed && successes == 0 && after == before &&
                            m_StaticPickup.IsConsumed;
            int expected = wonRace ? before + 1 : before;
            bool persisted = await CountRemainsAsync(controller, expected, 1.25f);
            bool pass = alreadyConsumed
                ? successes == 0 && m_StaticPickup.IsConsumed && persisted
                : (wonRace || lostRace) && persisted;

            AddResult(pass ? "PASS" : "FAIL",
                $"Static pickup exact-once: priorConsumed={alreadyConsumed}, successes={successes}, " +
                $"race={(wonRace ? "won" : lostRace ? "lost-validly" : "n/a")}, " +
                $"count {before}->{after}, persistedAcrossSync={persisted}");
        }

        private async Task TestStackSplitMoveAsync()
        {
            if (!RequireController(out NetworkInventoryController controller)) return;
            if (m_TestItem.Shape.MaxStack < 3)
            {
                AddResult("FAIL", $"'{m_TestItem.name}' only stacks to {m_TestItem.Shape.MaxStack}; regenerate with a stackable Item.");
                return;
            }

            Cell stack = FindTestStack(controller, 2);
            int attempts = 0;
            while (stack == null && attempts++ < 4)
            {
                NetworkContentAddResponse add = await controller.RequestAddItemAsync(
                    m_TestItem,
                    TBagContent.INVALID,
                    true,
                    InventoryModificationSource.Quest,
                    ValidatedSourceHash);
                if (!add.Authorized) break;
                await Task.Delay(200);
                stack = FindTestStack(controller, 2);
            }

            if (stack == null)
            {
                AddResult("FAIL", "Could not create a two-item stack in the local Bag.");
                return;
            }

            int totalBefore = CountTestItems(controller);
            int cellsBefore = CountTestCells(controller);
            long originalRootHash = stack.RootRuntimeItemID.Hash;
            var rootsBeforeSplit = new HashSet<long>();
            List<Cell> cellsBeforeSplit = GetTestCells(controller);
            for (int i = 0; i < cellsBeforeSplit.Count; i++)
                rootsBeforeSplit.Add(cellsBeforeSplit[i].RootRuntimeItemID.Hash);

            Vector2Int sourcePosition = controller.Bag.Content.FindPosition(stack.RootRuntimeItemID);
            TBagContent nativeContent = controller.Bag.Content as TBagContent;
            if (nativeContent == null)
            {
                AddResult("FAIL", "The local Bag does not expose GC2's patched TBagContent.");
                return;
            }

            Func<TBagContent, Vector2Int, int, NetworkInventoryInterceptResult> splitInterceptor =
                TBagContent.NetworkSplitInterceptor;
            if (splitInterceptor == null)
            {
                AddResult("FAIL", "The native GC2 Split interceptor is not installed.");
                return;
            }

            NetworkInventoryInterceptResult splitRoute = splitInterceptor.Invoke(
                nativeContent,
                sourcePosition,
                1);
            bool split = splitRoute == NetworkInventoryInterceptResult.HandledSuccess &&
                await WaitUntilAsync(() => CountTestCells(controller) > cellsBefore);

            if (!split)
            {
                AddResult("FAIL", $"Split did not converge (route={splitRoute}, cells={cellsBefore}->{CountTestCells(controller)}).");
                return;
            }

            List<Cell> testCells = GetTestCells(controller);
            Cell originalCell = null;
            Cell splitCell = null;
            for (int i = 0; i < testCells.Count; i++)
            {
                Cell candidate = testCells[i];
                long rootHash = candidate.RootRuntimeItemID.Hash;
                if (rootHash == originalRootHash) originalCell = candidate;
                else if (!rootsBeforeSplit.Contains(rootHash)) splitCell = candidate;
            }

            if (originalCell == null || splitCell == null)
            {
                AddResult("FAIL", "Split response arrived without the expected original/new stack identities.");
                return;
            }

            // The original stack just lost one item, so it has guaranteed room for the new split
            // stack even when the player's Bag already contained other stacks of the test Item.
            Vector2Int from = controller.Bag.Content.FindPosition(splitCell.RootRuntimeItemID);
            Vector2Int to = controller.Bag.Content.FindPosition(originalCell.RootRuntimeItemID);
            int splitCellCount = testCells.Count;
            bool nativeMoveAccepted = nativeContent.Move(from, to, true);
            bool moved = await WaitUntilAsync(() => CountTestCells(controller) < splitCellCount);
            int finalCells = CountTestCells(controller);
            bool snapshotApplied = moved && await ForceRecoverySnapshotAsync(controller);
            bool persisted = snapshotApplied && await InventoryShapeRemainsAsync(
                controller, totalBefore, finalCells, 1.25f);
            bool pass = nativeMoveAccepted && moved && snapshotApplied &&
                        CountTestItems(controller) == totalBefore && persisted;
            AddResult(pass ? "PASS" : "FAIL",
                $"Native Stack/Split/Move: moveAccepted={nativeMoveAccepted}, " +
                $"splitCells={splitCellCount}, finalCells={finalCells}, " +
                $"itemCount={totalBefore}->{CountTestItems(controller)}, " +
                $"recoverySnapshot={snapshotApplied}, persistedAcrossSync={persisted}");
        }

        private async Task TestReferenceStabilityAsync()
        {
            if (!RequireController(out NetworkInventoryController controller)) return;
            RuntimeItem captured = FindFirstTestRuntimeItem(controller);
            if (captured == null)
            {
                AddResult("FAIL", "Add the test Item before running the reference check.");
                return;
            }

            try
            {
                s_SelectedItemProperty?.SetValue(null, captured);
            }
            catch (Exception exception)
            {
                AddResult("WARN", $"Could not seed GC2 UI selection through reflection: {exception.Message}");
            }

            if (controller.IsServer)
            {
                AddResult("SKIP", "Snapshot selection/reference stability must be run on a connected client.");
                return;
            }

            bool snapshotApplied = await ForceRecoverySnapshotAsync(controller);
            RuntimeItem current = controller.Bag.Content.GetRuntimeItem(captured.RuntimeID);
            RuntimeItem selected = RuntimeItem.UI_LastItemSelected;
            bool sameReference = ReferenceEquals(captured, current);
            bool sameSelection = ReferenceEquals(captured, selected);
            AddResult(snapshotApplied && sameReference && sameSelection ? "PASS" : "FAIL",
                $"Recovery snapshot stability: applied={snapshotApplied}, " +
                $"sameReference={sameReference}, selectionPreserved={sameSelection}, " +
                $"runtime={captured.RuntimeID.String}");
        }

        private async Task<bool> ForceRecoverySnapshotAsync(
            NetworkInventoryController controller)
        {
            NetworkInventoryManager manager = NetworkInventoryManager.Instance;
            if (controller == null || controller.IsServer || manager == null) return false;

            uint applyCountBefore = controller.FullSnapshotApplyCount;
            ushort requestId = NextResyncRequestId();
            manager.SendResyncRequest(new NetworkInventoryResyncRequest
            {
                ActorNetworkId = controller.NetworkId,
                CorrelationId = NetworkCorrelation.Compose(controller.NetworkId, requestId),
                BagNetworkId = controller.NetworkId,
                LastAppliedStateVersion = 0
            });

            return await WaitUntilAsync(
                () => controller != null &&
                      controller.FullSnapshotApplyCount != applyCountBefore);
        }

        private ushort NextResyncRequestId()
        {
            m_ResyncRequestId++;
            if (m_ResyncRequestId == 0) m_ResyncRequestId = 1;
            return m_ResyncRequestId;
        }

        private bool RequireController(out NetworkInventoryController controller)
        {
            controller = m_LocalController;
            if (controller != null && m_TestItem != null) return true;
            AddResult("FAIL", controller == null
                ? "No ready local NetworkInventoryController was found."
                : "No test Item is configured.");
            return false;
        }

        private int CountTestItems(NetworkInventoryController controller)
        {
            return controller?.Bag?.Content == null || m_TestItem == null
                ? 0
                : controller.Bag.Content.CountType(m_TestItem);
        }

        private int CountTestCells(NetworkInventoryController controller)
        {
            return GetTestCells(controller).Count;
        }

        private List<Cell> GetTestCells(NetworkInventoryController controller)
        {
            var result = new List<Cell>();
            if (controller?.Bag?.Content == null || m_TestItem == null) return result;
            List<Cell> cells = controller.Bag.Content.CellList;
            for (int i = 0; i < cells.Count; i++)
            {
                Cell cell = cells[i];
                if (cell == null || cell.Available || cell.Item == null) continue;
                if (cell.Item.ID.Hash != m_TestItem.ID.Hash) continue;
                result.Add(cell);
            }
            return result;
        }

        private Cell FindTestStack(NetworkInventoryController controller, int minimumCount)
        {
            List<Cell> cells = GetTestCells(controller);
            for (int i = 0; i < cells.Count; i++)
                if (cells[i].Count >= minimumCount) return cells[i];
            return null;
        }

        private RuntimeItem FindFirstTestRuntimeItem(NetworkInventoryController controller)
        {
            List<Cell> cells = GetTestCells(controller);
            return cells.Count > 0 ? cells[0].RootRuntimeItem : null;
        }

        private static async Task<bool> WaitUntilAsync(Func<bool> predicate)
        {
            float started = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - started < OperationTimeout)
            {
                if (predicate.Invoke()) return true;
                await Task.Delay(100);
            }
            return predicate.Invoke();
        }

        private async Task<bool> CountRemainsAsync(
            NetworkInventoryController controller,
            int expectedCount,
            float durationSeconds)
        {
            float started = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - started < durationSeconds)
            {
                if (CountTestItems(controller) != expectedCount) return false;
                await Task.Delay(50);
            }
            return CountTestItems(controller) == expectedCount;
        }

        private async Task<bool> InventoryShapeRemainsAsync(
            NetworkInventoryController controller,
            int expectedItems,
            int expectedCells,
            float durationSeconds)
        {
            float started = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - started < durationSeconds)
            {
                if (CountTestItems(controller) != expectedItems ||
                    CountTestCells(controller) != expectedCells)
                {
                    return false;
                }
                await Task.Delay(50);
            }

            return CountTestItems(controller) == expectedItems &&
                   CountTestCells(controller) == expectedCells;
        }

        private void AddResult(string level, string message)
        {
            string line = $"[{level}] {message}";
            m_Results.Add(line);
            while (m_Results.Count > 20) m_Results.RemoveAt(0);
            Debug.Log($"[NetworkInventoryRegression] {line}", this);
        }

#if UNITY_EDITOR
        internal void EditorConfigure(
            Item item,
            NetworkInventoryPickupSource pickup,
            Trigger validatedAddTrigger,
            NetworkInventoryRegressionContinuationMarker validatedAddMarker,
            Trigger staticPickupTrigger,
            NetworkInventoryRegressionContinuationMarker staticPickupMarker)
        {
            m_TestItem = item;
            m_StaticPickup = pickup;
            m_ValidatedAddTrigger = validatedAddTrigger;
            m_ValidatedAddMarker = validatedAddMarker;
            m_StaticPickupTrigger = staticPickupTrigger;
            m_StaticPickupMarker = staticPickupMarker;
            UnityEditor.EditorUtility.SetDirty(this);
        }
#endif
    }

    /// <summary>Runtime marker used by the generated scene to prove instruction continuation.</summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("")]
    public sealed class NetworkInventoryRegressionContinuationMarker : MonoBehaviour
    {
        public int CompletedCount { get; private set; }

        internal void MarkCompleted()
        {
            CompletedCount++;
        }
    }

    /// <summary>
    /// Final instruction in each generated GC2 instruction list. It executes only when the
    /// authoritative Add Item/pickup instruction has completed successfully.
    /// </summary>
    [Serializable]
    public sealed class NetworkInventoryRegressionContinuationInstruction : Instruction
    {
        public override string Title => "Mark Inventory regression continuation";

        protected override Task Run(Args args)
        {
            args.Self?.GetComponent<NetworkInventoryRegressionContinuationMarker>()?.MarkCompleted();
            return DefaultResult;
        }
    }
}
#endif
