using System;
using System.Collections.Generic;

namespace EInvGstAddon.Core
{
    /// <summary>Read access to SAP Business One data (implemented over the DI API in production).</summary>
    public interface ISapSource
    {
        string DatabaseName { get; }
        SapInvoice GetInvoice(int docEntry);
        SapPartner GetPartner(string cardCode);
        SapAddress GetAddress(string cardCode, string addressCode, string type);
        SapItem GetItem(string itemCode);
        SapWarehouse GetWarehouse(string whsCode);
        List<SapWarehouse> ListWarehouses();
        int GetMaxDocEntry();
        /// <summary>A/R invoices (not cancelled) with DocEntry >= from that have no synchronisation record yet.</summary>
        List<int> ListUnrecordedInvoices(int fromDocEntry);
    }

    /// <summary>Persistent state in SAP (UDT @EINV_SYNC, mapping UDTs and invoice UDFs).</summary>
    public interface IStateStore
    {
        SyncRecord GetRecord(int docEntry);
        void SaveRecord(SyncRecord rec);
        List<SyncRecord> ListRecords(string status, int max);
        Dictionary<string, int> CountByStatus();

        MapEntry GetCustomerMap(string cardCode);
        void SaveCustomerMap(MapEntry e);
        MapEntry GetItemMap(string itemCode);
        void SaveItemMap(MapEntry e);
        MapEntry GetWarehouseMap(string whsCode);
        void SaveWarehouseMap(MapEntry e);
        void RemoveCustomerMap(string cardCode);
        void RemoveItemMap(string itemCode);
        void RemoveWarehouseMap(string whsCode);
    }

    public class ErpUnavailableException : Exception
    {
        public ErpUnavailableException(string m, Exception inner = null) : base(m, inner) { }
    }

    public class ErpAuthException : Exception
    {
        public ErpAuthException(string m) : base(m) { }
    }

    /// <summary>ERPNext rejected the request (validation, missing link, ...). Not retryable without a data/config change.</summary>
    public class ErpApiException : Exception
    {
        public int StatusCode;
        public string ExcType;
        public ErpApiException(string m, int status, string excType) : base(m) { StatusCode = status; ExcType = excType; }
    }

    /// <summary>A required mapping/configuration/master datum is missing. Reported to the user; never guessed.</summary>
    public class ConfigurationException : Exception
    {
        public ConfigurationException(string m) : base(m) { }
    }
}

namespace EInvGstAddon.Core
{
    /// <summary>Runs a function on the thread that owns the SAP UI/DI API objects (the add-on's main STA thread).</summary>
    public interface IUiInvoker
    {
        T Invoke<T>(System.Func<T> f);
    }

    public sealed class DirectInvoker : IUiInvoker
    {
        public T Invoke<T>(System.Func<T> f) { return f(); }
    }
}
