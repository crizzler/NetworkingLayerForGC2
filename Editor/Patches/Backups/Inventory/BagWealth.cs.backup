using System;
using System.Collections.Generic;
using GameCreator.Runtime.Common;
using UnityEngine;

namespace GameCreator.Runtime.Inventory
{
    [Serializable]
    public class BagWealth : IBagWealth
    {
        // EXPOSED MEMBERS: -----------------------------------------------------------------------
        
        [SerializeField] private BagWealthMap m_Currencies = new BagWealthMap();
        
        // PROPERTIES: ----------------------------------------------------------------------------

        public List<IdString> List => new List<IdString>(this.m_Currencies.Keys);
        
        // EVENTS: --------------------------------------------------------------------------------

        public event Action<IdString, int, int> EventChange;

        // INITIALIZERS: --------------------------------------------------------------------------
        
        public void OnLoad(TokenBagWealth tokenBagWealth)
        {
            foreach (TokenBagWealth.Data wealthData in tokenBagWealth.Wealth)
            {
                this.Set(wealthData.CurrencyID, wealthData.Amount);
            }
        }
        
        // GETTERS: -------------------------------------------------------------------------------

        public int Get(Currency currency)
        {
            return currency != null ? this.Get(currency.ID) : 0;
        }
        
        public int Get(IdString currencyID)
        {
            return this.m_Currencies.TryGetValue(currencyID, out int amount) ? amount : 0;
        }
        
        // SETTERS: -------------------------------------------------------------------------------

        public void Set(Currency currency, int value)
        {
            if (currency == null) return;
            this.Set(currency.ID, value);
        }
        
        public void Set(IdString currencyID, int value)
        {
            int prevAmount = this.Get(currencyID);
            this.m_Currencies[currencyID] = value;
            int newAmount = this.Get(currencyID);
            
            this.EventChange?.Invoke(currencyID, prevAmount, newAmount);
        }

        public void Add(Currency currency, int value)
        {
            if (currency == null) return;
            this.Add(currency.ID, value);
        }
        
        public void Add(IdString currencyID, int value)
        {
            value = Mathf.Max(this.Get(currencyID) + value, 0);
            this.Set(currencyID, value);
        }

        public void Subtract(Currency currency, int value)
        {
            this.Add(currency, -value);
        }

        public void Subtract(IdString currencyID, int value)
        {
            this.Add(currencyID, -value);
        }
    }
}