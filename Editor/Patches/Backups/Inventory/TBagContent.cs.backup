using System;
using System.Collections.Generic;
using GameCreator.Runtime.Common;
using UnityEngine;

namespace GameCreator.Runtime.Inventory
{
    [Serializable]
    public abstract class TBagContent : IBagContent
    {
        public static readonly Vector2Int INVALID = new Vector2Int(-1, -1);
        
        // CLASSES: -------------------------------------------------------------------------------
        
        [Serializable]
        protected class Cells : TSerializableDictionary<IdString, Cell>
        { }
        
        [Serializable]
        protected class Stack : TSerializableDictionary<IdString, IdString>
        { }

        // EVENTS: --------------------------------------------------------------------------------

        public event Action EventChange;
        public event Action<RuntimeItem> EventUse;
        
        public event Action<RuntimeItem> EventAdd;
        public event Action<RuntimeItem> EventRemove;

        // PROPERTIES: ----------------------------------------------------------------------------

        [field: NonSerialized] protected Bag Bag { get; private set; }

        public abstract List<Cell> CellList { get; }
        public abstract List<Bucket> RuntimeItemsClone { get; }
        
        public abstract bool CellsAsChunks { get; }

        public int CountWithStack
        {
            get
            {
                int count = 0;
                List<Cell> items = this.CellList;
                foreach (Cell cell in items)
                {
                    count += cell.Count;
                }

                return count;
            }
        }

        public int CountWithoutStack => this.CellList.Count;

        public virtual int CurrentWeight
        {
            get
            {
                int weight = 0;
                List<Cell> items = this.CellList;
                foreach (Cell cell in items)
                {
                    weight += cell.Weight;
                }
                
                return weight;
            }
        }
        
        // CHECKERS: ------------------------------------------------------------------------------
        
        public abstract bool IsAvailable(Vector2Int position, Vector2Int size);

        public abstract bool CanAdd(RuntimeItem runtimeItem, bool allowStack);
        public abstract bool CanAddType(Item item, bool allowStack);
        
        public abstract bool CanMove(Vector2Int positionA, Vector2Int positionB, bool allowStack);

        public abstract bool Contains(IdString runtimeItemID);
        public abstract bool Contains(RuntimeItem runtimeItem);
        public abstract bool ContainsType(Item item, int amount);
        
        // GETTERS: -------------------------------------------------------------------------------

        public virtual RuntimeItem FindRuntimeItem(Item item)
        {
            foreach (Cell cell in this.CellList)
            {
                if (cell == null || cell.Available) continue;
                if (!cell.Item.InheritsFrom(item.ID)) continue;

                return cell.RootRuntimeItem;
            }

            return null;
        }
        
        public abstract RuntimeItem GetRuntimeItem(IdString runtimeItemID);
        public abstract Cell GetContent(Vector2Int position);
        
        public abstract Vector2Int FindPosition(IdString runtimeItemID);
        public abstract Vector2Int FindStartPosition(Vector2Int position);

        public virtual Cell[] GetTypes(Item item)
        {
            List<Cell> result = new List<Cell>();
            List<Cell> cells = this.CellList;
            
            foreach (Cell cell in cells)
            {
                if (cell == null || cell.Available) continue;
                if (!cell.Item.InheritsFrom(item.ID)) continue;

                result.Add(cell);
            }

            return result.ToArray();
        }
        
        public virtual int CountType(Item item)
        {
            int count = 0;
            List<Cell> cells = this.CellList;
            
            foreach (Cell cell in cells)
            {
                if (cell == null || cell.Available) continue;
                if (!cell.Item.InheritsFrom(item.ID)) continue;

                count += cell.Count;
            }

            return count;
        }

        public abstract int CountFreeSpaces();

        // SETTERS: -------------------------------------------------------------------------------
        
        public abstract bool Move(Vector2Int positionA, Vector2Int positionB, bool allowStack);
        public abstract bool Add(RuntimeItem runtimeItem, Vector2Int position, bool allowStack);
        public abstract Vector2Int Add(RuntimeItem runtimeItem, bool allowStack);
        
        public abstract RuntimeItem AddType(Item item, Vector2Int position, bool allowStack);
        public abstract RuntimeItem AddType(Item item, bool allowStack);
        
        public abstract RuntimeItem Remove(Vector2Int position);
        public abstract RuntimeItem Remove(RuntimeItem runtimeItem);
        public abstract RuntimeItem RemoveType(Item item);

        public abstract void Sort(Func<int, int, int> comparison);
        
        public virtual bool Use(RuntimeItem runtimeItem)
        {
            if (!this.Contains(runtimeItem)) return false;
            if (!runtimeItem.CanUse()) return false;

            _ = runtimeItem.Use();
            if (runtimeItem.Item.Usage.ConsumeWhenUse) this.Remove(runtimeItem);
            
            this.EventUse?.Invoke(runtimeItem);
            return true;
        }
        
        public virtual bool Use(Vector2Int position)
        {
            Cell cell = this.GetContent(position);
            if (cell == null || cell.Available) return false;

            RuntimeItem runtimeItem = cell.Peek();
            return this.Use(runtimeItem);
        }

        public GameObject Drop(RuntimeItem runtimeItem, Vector3 point)
        {
            if (runtimeItem == null) return null;
            if (!this.Contains(runtimeItem)) return null;
            
            if (this.Bag.Wearer == null) return null;
            if (!runtimeItem.Item.HasPrefab) return null;
            if (!runtimeItem.Item.CanDrop) return null;

            RuntimeItem removeRuntimeItem = this.Remove(runtimeItem);
            return Item.Drop(removeRuntimeItem, point, Quaternion.identity);
        }

        public GameObject[] Drop(Vector2Int position, int maxAmount, Vector3 point)
        {
            List<GameObject> instances = new List<GameObject>();
            
            Cell cell = this.GetContent(position);
            if (cell == null || cell.Available) return instances.ToArray();

            maxAmount = Math.Min(cell.Count, maxAmount);
            for (int i = 0; i < maxAmount; ++i)
            {
                RuntimeItem runtimeItem = cell.Peek();
                GameObject instance = this.Drop(runtimeItem, point); 
                if (instance != null) instances.Add(instance);
            }

            return instances.ToArray();
        }

        public virtual bool Combine(Vector2Int positionA, Vector2Int positionB)
        {
            RuntimeItem runtimeItemA = this.GetContent(positionA)?.RootRuntimeItem;
            RuntimeItem runtimeItemB = this.GetContent(positionB)?.RootRuntimeItem;
            
            if (runtimeItemA == null || runtimeItemB == null) return false;
            if (runtimeItemA == runtimeItemB) return false;

            foreach (Item item in InventoryRepository.Get.Items.List)
            {
                if (!item.Crafting.AllowToCraft || !item.Crafting.AllowToCombine) continue; 
                if (item.Crafting.Ingredients.Length != 2) continue;
                
                Ingredient ingredient1 = item.Crafting.Ingredients[0];
                Ingredient ingredient2 = item.Crafting.Ingredients[1];
                
                if (ingredient1.Item == null || ingredient2.Item == null) continue;

                bool combinationA = ingredient1.Item == runtimeItemA.Item && ingredient2.Item == runtimeItemB.Item;
                bool combinationB = ingredient1.Item == runtimeItemB.Item && ingredient2.Item == runtimeItemA.Item;
                
                if (!combinationA && !combinationB) continue;

                Bag bag = runtimeItemA.Bag;
                if (Crafting.CanCraft(item, bag, bag) && Crafting.EnoughCraftingIngredients(item, bag))
                {
                    Crafting.Craft(item, bag, bag);
                    return true;
                }
            }
            
            return false;
        }

        // PUBLIC METHODS: ------------------------------------------------------------------------

        public virtual void OnAwake(Bag bag)
        {
            this.Bag = bag;
            this.Bag.Shape.EventChange += this.OnChangeShape;
        }
        
        public virtual void OnLoad(TokenBagItems tokenBagItems)
        {
            for (int i = this.CellList.Count - 1; i >= 0; --i)
            {
                Cell cell = this.CellList[i];
                if (cell.Available) continue;

                for (int j = cell.List.Count - 1; j >= 0; --j)
                {
                    IdString runtimeItemID = cell.List[j];
                    RuntimeItem runtimeItem = this.GetRuntimeItem(runtimeItemID);
                    this.Remove(runtimeItem);
                }
            }

            int indexX = 0;
            int indexY = 0;
            
            foreach (Bucket bucket in tokenBagItems.RuntimeItems)
            {
                RuntimeItem[] runtimeItems = bucket.RuntimeItems;
                for (int i = 0; i < runtimeItems.Length; i++)
                {
                    RuntimeItem runtimeItem = runtimeItems[i];
                    this.Add(runtimeItem, new Vector2Int(indexX, indexY), i != 0);
                }

                if (indexX == this.Bag.Shape.MaxWidth - 1)
                {
                    indexX = 0;
                    indexY += 1;
                }
                else
                {
                    indexX += 1;
                }
            }
        }
        
        // PROTECTED METHODS: ---------------------------------------------------------------------

        protected void ExecuteEventChange()
        {
            this.EventChange?.Invoke();
        }

        protected void ExecuteEventAdd(RuntimeItem runtimeItem)
        {
            this.EventAdd?.Invoke(runtimeItem);
        }

        protected void ExecuteEventRemove(RuntimeItem runtimeItem)
        {
            this.EventRemove?.Invoke(runtimeItem);
        }
        
        protected virtual void OnChangeShape()
        { }
    }
}