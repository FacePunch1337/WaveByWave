using Unity.Netcode;
using UnityEngine;
using WaveByWave.Items;

namespace WaveByWave.Commerce
{
    [RequireComponent(typeof(NetworkObject))]
    public sealed class OrderDeliveryCart : NetworkBehaviour
    {
        [SerializeField] private ItemCatalog catalog;
        [SerializeField] private WorldItem worldItemPrefab;
        [SerializeField] private Transform deliveryOrigin;
        [SerializeField, Min(0.1f)] private float itemSpacing = 0.55f;
        [SerializeField, Min(1f)] private float dayLengthSeconds = 300f;
        [SerializeField] private bool automaticallyAdvanceDays = true;
        [SerializeField] private bool createTemporaryVisual;

        private readonly NetworkVariable<int> _currentDay = new(1);
        private float _dayTimer;

        public int CurrentDay => _currentDay.Value;

        private void Awake()
        {
            if (!createTemporaryVisual || GetComponentInChildren<Renderer>() != null)
                return;
            var visual = GameObject.CreatePrimitive(PrimitiveType.Cube);
            visual.name = "Temporary SM_Furniture_Cart_01";
            visual.transform.SetParent(transform, false);
            visual.transform.localScale = new Vector3(1.7f, 0.45f, 1.1f);
            visual.GetComponent<Collider>().enabled = false;
            visual.GetComponent<Renderer>().material.color = new Color(0.34f, 0.2f, 0.1f);
        }

        private void Update()
        {
            if (!IsServer || !automaticallyAdvanceDays)
                return;
            _dayTimer += Time.deltaTime;
            if (_dayTimer < dayLengthSeconds)
                return;
            var elapsedDays = Mathf.FloorToInt(_dayTimer / dayLengthSeconds);
            _dayTimer -= elapsedDays * dayLengthSeconds;
            _currentDay.Value += elapsedDays;
        }

        [ContextMenu("Advance Day (Server)")]
        public void AdvanceDayServer()
        {
            if (IsServer)
                _currentDay.Value++;
        }

        public bool ReceiveDeliveryServer(OrderLineState[] lines)
        {
            if (!IsServer || worldItemPrefab == null || lines == null)
                return false;

            var spawnedIndex = 0;
            foreach (var line in lines)
            {
                if (line.Quantity == 0 || catalog == null || !catalog.TryGet(line.ItemId.ToString(), out var definition))
                    continue;
                var remaining = (int)line.Quantity;
                while (remaining > 0)
                {
                    var amount = Mathf.Min(remaining, definition.MaximumStack);
                    var origin = deliveryOrigin != null ? deliveryOrigin : transform;
                    var column = spawnedIndex % 4;
                    var row = spawnedIndex / 4;
                    var localOffset = new Vector3((column - 1.5f) * itemSpacing, 0.2f + row * 0.18f,
                        (row % 2 - 0.5f) * itemSpacing);
                    var position = origin.TransformPoint(localOffset);
                    var item = Instantiate(worldItemPrefab, position, origin.rotation);
                    item.SetState(line.ItemId, (ushort)amount);
                    item.PlaceSettledServer(position, origin.rotation, NetworkObject);
                    item.NetworkObject.Spawn();
                    remaining -= amount;
                    spawnedIndex++;
                }
            }
            return spawnedIndex > 0;
        }
    }
}
