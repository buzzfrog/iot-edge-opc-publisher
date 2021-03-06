﻿// ------------------------------------------------------------
//  Copyright (c) Microsoft Corporation.  All rights reserved.
//  Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using Opc.Ua.Client;
using System;
using System.Collections.Generic;
using System.Linq;

namespace OpcPublisher
{
    /// <summary>
    /// Class to encapsulate OPC UA subscription API.
    /// </summary>
    public class OpcUaSubscriptionWrapper
    {
        public uint Id => _subscription.Id;

        public uint MonitoredItemCount => _subscription.MonitoredItemCount;

        public IEnumerable<MonitoredItem> MonitoredItems => _subscription.MonitoredItems;

        public int PublishingInterval
        {
            get
            {
                return _subscription.PublishingInterval;

            }
            set
            {
                _subscription.PublishingInterval = value;
            }
        }

        public Subscription Subscription => _subscription;

        public OpcUaSubscriptionWrapper(Subscription defaultSubscription)
        {
            _subscription = new Subscription(defaultSubscription);
        }

        /// <summary>
        /// Implement IDisposable.
        /// </summary>
        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                _subscription?.Dispose();
                _subscription = null;
            }
        }

        /// <summary>
        /// Implement IDisposable.
        /// </summary>
        public void Dispose()
        {
            // do cleanup
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        public void AddItem(OpcUaMonitoredItemWrapper monitoredItem) => _subscription.AddItem(monitoredItem.MonitoredItem);

        public void AddItem(MonitoredItem monitoredItem) => _subscription.AddItem(monitoredItem);

        public void ApplyChanges() => _subscription.ApplyChanges();

        public void Create() => _subscription.Create();

        public void Delete(bool silent) => _subscription.Delete(silent);

        public void RemoveItems(IEnumerable<OpcUaMonitoredItemWrapper> monitoredItems) => _subscription.RemoveItems(monitoredItems.Select(m => m.MonitoredItem));

        public void RemoveItems(IEnumerable<MonitoredItem> monitoredItems) => _subscription.RemoveItems(monitoredItems);

        public void SetPublishingMode(bool enabled) => _subscription.SetPublishingMode(enabled);


        private Subscription _subscription;
    }
}
