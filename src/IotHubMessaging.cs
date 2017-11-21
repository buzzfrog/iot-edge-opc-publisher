﻿
using System.Collections.Concurrent;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace OpcPublisher
{
    using Microsoft.Azure.Devices;
    using Microsoft.Azure.Devices.Client;
    using Opc.Ua;
    using System;
    using System.Diagnostics;
    using System.IO;
    using static Opc.Ua.CertificateStoreType;
    using static OpcPublisher.Diagnostics;
    using static OpcPublisher.Workarounds.TraceWorkaround;
    using static OpcStackConfiguration;

    /// <summary>
    /// Class to handle all IoTHub communication.
    /// </summary>
    public class IotHubMessaging
    {
        public static string IotDeviceCertDirectoryStorePathDefault => "CertificateStores/IoTHub";

        public static string IotDeviceCertX509StorePathDefault => "My";

        public static long MonitoredItemsQueueCount => _monitoredItemsDataQueue.Count;

        public static long DequeueCount => _dequeueCount;

        public static long MissedSendIntervalCount => _missedSendIntervalCount;

        public static long TooLargeCount => _tooLargeCount;

        public static long SentBytes => _sentBytes;

        public static long SentMessages => _sentMessages;

        public static DateTime SentLastTime => _sentLastTime;

        public static long FailedMessages => _failedMessages;

        public static string IotHubOwnerConnectionString
        {
            get => _iotHubOwnerConnectionString;
            set => _iotHubOwnerConnectionString = value;
        }

        public static Microsoft.Azure.Devices.Client.TransportType IotHubProtocol
        {
            get => _iotHubProtocol;
            set => _iotHubProtocol = value;
        }

        public const uint IotHubMessageSizeMax = (256 * 1024);
        public static uint IotHubMessageSize
        {
            get => _iotHubMessageSize;
            set => _iotHubMessageSize = value;
        }

        public static int DefaultSendIntervalSeconds
        {
            get => _defaultSendIntervalSeconds;
            set => _defaultSendIntervalSeconds = value;
        }

        public static string IotDeviceCertStoreType
        {
            get => _iotDeviceCertStoreType;
            set => _iotDeviceCertStoreType = value;
        }

        public static string IotDeviceCertStorePath
        {
            get => _iotDeviceCertStorePath;
            set => _iotDeviceCertStorePath = value;
        }

        public static int MonitoredItemsQueueCapacity
        {
            get => _monitoredItemsQueueCapacity;
            set => _monitoredItemsQueueCapacity = value;
        }

        public static long EnqueueCount
        {
            get
            {
                DiagnosticsSemaphore.WaitAsync();
                long result = _enqueueCount;
                DiagnosticsSemaphore.Release();
                return result;
            }
        }

        public static long EnqueueFailureCount
        {
            get
            {
                DiagnosticsSemaphore.WaitAsync();
                long result = _enqueueFailureCount;
                DiagnosticsSemaphore.Release();
                return result;
            }
        }

        /// <summary>
        /// Initializes the communication with secrets and details for (batched) send process.
        /// </summary>
        public async Task<bool> InitAsync()
        {
            try
            {
                // check if we also received an owner connection string
                if (string.IsNullOrEmpty(_iotHubOwnerConnectionString))
                {
                    Trace("IoT Hub owner connection string not passed as argument.");

                    // check if we have an environment variable to register ourselves with IoT Hub
                    if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("_HUB_CS")))
                    {
                        _iotHubOwnerConnectionString = Environment.GetEnvironmentVariable("_HUB_CS");
                        Trace("IoT Hub owner connection string read from environment.");
                    }
                }

                // register ourselves with IoT Hub
                string deviceConnectionString;
                Trace($"IoTHub device cert store type is: {IotDeviceCertStoreType}");
                Trace($"IoTHub device cert path is: {IotDeviceCertStorePath}");
                if (string.IsNullOrEmpty(_iotHubOwnerConnectionString))
                {
                    Trace("IoT Hub owner connection string not specified. Assume device connection string already in cert store.");
                }
                else
                {
                    Trace($"Attempting to register ourselves with IoT Hub using owner connection string: {_iotHubOwnerConnectionString}");
                    RegistryManager manager = RegistryManager.CreateFromConnectionString(_iotHubOwnerConnectionString);

                    // remove any existing device
                    Device existingDevice = await manager.GetDeviceAsync(ApplicationName);
                    if (existingDevice != null)
                    {
                        Trace($"Device '{ApplicationName}' found in IoTHub registry. Remove it.");
                        await manager.RemoveDeviceAsync(ApplicationName);
                    }

                    Trace($"Adding device '{ApplicationName}' to IoTHub registry.");
                    Device newDevice = await manager.AddDeviceAsync(new Device(ApplicationName));
                    if (newDevice != null)
                    {
                        string hostname = _iotHubOwnerConnectionString.Substring(0, _iotHubOwnerConnectionString.IndexOf(";"));
                        deviceConnectionString = hostname + ";DeviceId=" + ApplicationName + ";SharedAccessKey=" + newDevice.Authentication.SymmetricKey.PrimaryKey;
                        Trace($"Device connection string is: {deviceConnectionString}");
                        Trace($"Adding it to device cert store.");
                        await SecureIoTHubToken.WriteAsync(ApplicationName, deviceConnectionString, IotDeviceCertStoreType, IotDeviceCertStorePath);
                    }
                    else
                    {
                        Trace($"Could not register ourselves with IoT Hub using owner connection string: {_iotHubOwnerConnectionString}");
                        Trace("exiting...");
                        return false;
                    }
                }

                // try to read connection string from secure store and open IoTHub client
                Trace($"Attempting to read device connection string from cert store using subject name: {ApplicationName}");
                deviceConnectionString = await SecureIoTHubToken.ReadAsync(ApplicationName, IotDeviceCertStoreType, IotDeviceCertStorePath);
                if (!string.IsNullOrEmpty(deviceConnectionString))
                {
                    Trace($"Create Publisher IoTHub client with device connection string: '{deviceConnectionString}' using '{IotHubProtocol}' for communication.");
                    _iotHubClient = DeviceClient.CreateFromConnectionString(deviceConnectionString, IotHubProtocol);
                    ExponentialBackoff exponentialRetryPolicy = new ExponentialBackoff(int.MaxValue, TimeSpan.FromMilliseconds(2), TimeSpan.FromMilliseconds(1024), TimeSpan.FromMilliseconds(3));
                    _iotHubClient.SetRetryPolicy(exponentialRetryPolicy);
                    await _iotHubClient.OpenAsync();
                }
                else
                {
                    Trace("Device connection string not found in secure store. Could not connect to IoTHub.");
                    Trace("exiting...");
                    return false;
                }

                // show config
                Trace($"IoTHub messaging configured with a send interval of {_defaultSendIntervalSeconds} sec and a message buffer size of {_iotHubMessageSize} bytes.");

                // create the queue for monitored items
                _monitoredItemsDataQueue = new BlockingCollection<string>(_monitoredItemsQueueCapacity);

                // start up task to send telemetry to IoTHub
                _monitoredItemsProcessorTask = null;
                _tokenSource = new CancellationTokenSource();

                Trace("Creating task process and batch monitored item data updates...");
                _monitoredItemsProcessorTask = Task.Run(async () => await MonitoredItemsProcessor(_tokenSource.Token), _tokenSource.Token);
            }
            catch (Exception e)
            {
                Trace(e, $"Error in InitAsync. (message: {e.Message})");
                return false;
            }
            return true;
        }

        /// <summary>
        /// Method to write the IoTHub owner connection string into the cert store. 
        /// </summary>
        public async Task ConnectionStringWriteAsync(string iotHubOwnerConnectionString)
        {
            DeviceClient newClient = DeviceClient.CreateFromConnectionString(iotHubOwnerConnectionString, IotHubProtocol);
            await newClient.OpenAsync();
            await SecureIoTHubToken.WriteAsync(PublisherOpcApplicationConfiguration.ApplicationName, iotHubOwnerConnectionString, IotDeviceCertStoreType, IotDeviceCertStorePath);
            _iotHubClient = newClient;
        }

        /// <summary>
        /// Shuts down the IoTHub communication.
        /// </summary>
        public async Task Shutdown()
        {
            // send cancellation token and wait for last IoT Hub message to be sent.
            try
            {
                _tokenSource.Cancel();
                await _monitoredItemsProcessorTask;

                if (_iotHubClient != null)
                {
                    await _iotHubClient.CloseAsync();
                }

                _monitoredItemsDataQueue = null;
                _tokenSource = null;
                _monitoredItemsProcessorTask = null;
                _iotHubClient = null;
            }
            catch (Exception e)
            {
                Trace(e, "Failure while shutting down IoTHub messaging.");
            }
        }

        /// <summary>
        /// Enqueue a message for sending to IoTHub.
        /// </summary>
        /// <param name="json"></param>
        public void Enqueue(string json)
        {
            // Try to add the message.
            DiagnosticsSemaphore.WaitAsync();
            _enqueueCount++;
            DiagnosticsSemaphore.Release();
            if (_monitoredItemsDataQueue.TryAdd(json) == false)
            {
                DiagnosticsSemaphore.WaitAsync();
                long failureCount = ++_enqueueFailureCount;
                DiagnosticsSemaphore.Release();
                if (failureCount % 10000 == 0)
                {
                    Trace(Utils.TraceMasks.Error, $"The internal monitored item message queue is above its capacity of {_monitoredItemsDataQueue.BoundedCapacity}. We have already lost {failureCount} monitored item notifications:(");
                }
            }
        }

        /// <summary>
        /// Dequeue monitored item notification messages, batch them for send (if needed) and send them to IoTHub.
        /// </summary>
        private async Task MonitoredItemsProcessor(CancellationToken ct)
        {
            uint jsonSquareBracketLength = 2;
            Microsoft.Azure.Devices.Client.Message tempMsg = new Microsoft.Azure.Devices.Client.Message();
            // the system properties are MessageId (max 128 byte), Sequence number (ulong), ExpiryTime (DateTime) and more. ideally we get that from the client.
            int systemPropertyLength = 128 + sizeof(ulong) + tempMsg.ExpiryTimeUtc.ToString().Length;
            // if batching is requested the buffer will have the requested size, otherwise we reserve the max size
            uint iotHubMessageBufferSize = (_iotHubMessageSize > 0 ? _iotHubMessageSize : IotHubMessageSizeMax) - (uint)systemPropertyLength - (uint)jsonSquareBracketLength;
            byte[] iotHubMessageBuffer = new byte[iotHubMessageBufferSize];
            MemoryStream iotHubMessage = new MemoryStream(iotHubMessageBuffer);
            DateTime nextSendTime = DateTime.UtcNow + TimeSpan.FromSeconds(_defaultSendIntervalSeconds);
            double millisecondsTillNextSend = nextSendTime.Subtract(DateTime.UtcNow).TotalMilliseconds;

            using (iotHubMessage)
            {
                try
                {
                    string jsonMessage = string.Empty;
                    bool needToBufferMessage = false;
                    int jsonMessageSize = 0;

                    iotHubMessage.Position = 0;
                    iotHubMessage.SetLength(0);
                    iotHubMessage.Write(Encoding.UTF8.GetBytes("["), 0, 1);
                    while (true)
                    {
                        // sanity check the send interval, compute the timeout and get the next monitored item message
                        if (_defaultSendIntervalSeconds > 0)
                        {
                            millisecondsTillNextSend = nextSendTime.Subtract(DateTime.UtcNow).TotalMilliseconds;
                            if (millisecondsTillNextSend < 0)
                            {
                                _missedSendIntervalCount++;
                                // do not wait if we missed the send interval
                                millisecondsTillNextSend = 0;
                            }
                        }
                        else
                        {
                            // if we are in shutdown do not wait, else wait infinite if send interval is not set
                            millisecondsTillNextSend = ct.IsCancellationRequested ? 0 : Timeout.Infinite;
                        }
                        bool gotItem = _monitoredItemsDataQueue.TryTake(out jsonMessage, (int)millisecondsTillNextSend, ct);

                        // the two commandline parameter --ms (message size) and --si (send interval) control when data is sent to IoTHub
                        // pls see detailed comments on performance and memory consumption at https://github.com/Azure/iot-edge-opc-publisher

                        // check if we got an item or if we hit the timeout or got canceled
                        if (gotItem)
                        {
                            _dequeueCount++;
                            jsonMessageSize = Encoding.UTF8.GetByteCount(jsonMessage.ToString());

                            // sanity check that the user has set a large enough IoTHub messages size
                            if ((_iotHubMessageSize > 0 && jsonMessageSize > _iotHubMessageSize) || (_iotHubMessageSize == 0 && jsonMessageSize > iotHubMessageBufferSize))
                            {
                                Trace(Utils.TraceMasks.Error, $"There is a telemetry message (size: {jsonMessageSize}), which will not fit into an IoTHub message (max size: {iotHubMessageBufferSize}].");
                                Trace(Utils.TraceMasks.Error, $"Please check your IoTHub message size settings. The telemetry message will be discarded silently. Sorry:(");
                                _tooLargeCount++;
                                continue;
                            }

                            // if batching is requested or we need to send at intervals, batch it otherwise send it right away
                            needToBufferMessage = false;
                            if (_iotHubMessageSize > 0 || (_iotHubMessageSize == 0 && _defaultSendIntervalSeconds > 0))
                            {
                                // if there is still space to batch, do it. otherwise send the buffer and flag the message for later buffering
                                if (iotHubMessage.Position + jsonMessageSize + 1 <= iotHubMessage.Capacity)
                                {
                                    // add the message and a comma to the buffer
                                    iotHubMessage.Write(Encoding.UTF8.GetBytes(jsonMessage.ToString()), 0, jsonMessageSize);
                                    iotHubMessage.Write(Encoding.UTF8.GetBytes(","), 0, 1);
                                    Trace(Utils.TraceMasks.OperationDetail, $"Added new message with size {jsonMessageSize} to IoTHub message (size is now {(iotHubMessage.Position - 1)}).");
                                    continue;
                                }
                                else
                                {
                                    needToBufferMessage = true;
                                }
                            }
                        }
                        else
                        {
                            // if we got no message, we either reached the interval or we are in shutdown and have processed all messages
                            if (ct.IsCancellationRequested)
                            {
                                Trace($"Cancellation requested.");
                                _monitoredItemsDataQueue.CompleteAdding();
                                _monitoredItemsDataQueue.Dispose();
                                break;
                            }
                        }

                        // the batching is completed or we reached the send interval or got a cancelation request
                        try
                        {
                            Microsoft.Azure.Devices.Client.Message encodedIotHubMessage = null;

                            // if we reached the send interval, but have nothing to send, we continue
                            if (!gotItem && iotHubMessage.Position == 1)
                            {
                                nextSendTime += TimeSpan.FromSeconds(_defaultSendIntervalSeconds);
                                iotHubMessage.Position = 0;
                                iotHubMessage.SetLength(0);
                                iotHubMessage.Write(Encoding.UTF8.GetBytes("["), 0, 1);
                                continue;
                            }

                            // if there is no batching and not interval configured, we send the JSON message we just got, otherwise we send the buffer
                            if (_iotHubMessageSize == 0 && _defaultSendIntervalSeconds == 0)
                            {
                                // we use also an array for a single message to make backend processing more consistent
                                encodedIotHubMessage = new Microsoft.Azure.Devices.Client.Message(Encoding.UTF8.GetBytes("[" + jsonMessage.ToString() + "]"));
                            }
                            else
                            {
                                // remove the trailing comma and add a closing square bracket
                                iotHubMessage.SetLength(iotHubMessage.Length - 1);
                                iotHubMessage.Write(Encoding.UTF8.GetBytes("]"), 0, 1);
                                encodedIotHubMessage = new Microsoft.Azure.Devices.Client.Message(iotHubMessage.ToArray());
                            }
                            if (_iotHubClient != null)
                            {
                                nextSendTime += TimeSpan.FromSeconds(_defaultSendIntervalSeconds);
                                try
                                {
                                    _sentBytes += encodedIotHubMessage.GetBytes().Length;
                                    await _iotHubClient.SendEventAsync(encodedIotHubMessage);
                                    _sentMessages++;
                                    _sentLastTime = DateTime.UtcNow;
                                    Trace(Utils.TraceMasks.OperationDetail, $"Sending {encodedIotHubMessage.BodyStream.Length} bytes to IoTHub.");
                                }
                                catch
                                {
                                    _failedMessages++;
                                }

                                // reset the messaage
                                iotHubMessage.Position = 0;
                                iotHubMessage.SetLength(0);
                                iotHubMessage.Write(Encoding.UTF8.GetBytes("["), 0, 1);

                                // if we had not yet buffered the last message because there was not enough space, buffer it now
                                if (needToBufferMessage)
                                {
                                    // add the message and a comma to the buffer
                                    iotHubMessage.Write(Encoding.UTF8.GetBytes(jsonMessage.ToString()), 0, jsonMessageSize);
                                    iotHubMessage.Write(Encoding.UTF8.GetBytes(","), 0, 1);
                                }
                            }
                            else
                            {
                                Trace("No IoTHub client available. Dropping messages...");
                            }
                        }
                        catch (Exception e)
                        {
                            Trace(e, "Exception while sending message to IoTHub. Dropping message...");
                        }
                    }
                }
                catch (Exception e)
                {
                    if (!(e is OperationCanceledException))
                    {

                        Trace(e, "Error while processing monitored item messages.");
                    }
                }
            }
        }

        private static string _iotHubOwnerConnectionString = string.Empty;
        private static Microsoft.Azure.Devices.Client.TransportType _iotHubProtocol = Microsoft.Azure.Devices.Client.TransportType.Mqtt_WebSocket_Only;
        private static uint _iotHubMessageSize = 262144;
        private static int _defaultSendIntervalSeconds = 10;
        private static string _iotDeviceCertStoreType = X509Store;
        private static string _iotDeviceCertStorePath = IotDeviceCertX509StorePathDefault;
        private static int _monitoredItemsQueueCapacity = 8192;
        private static long _enqueueCount;
        private static long _enqueueFailureCount;
        private static long _dequeueCount;
        private static long _missedSendIntervalCount;
        private static long _tooLargeCount;
        private static long _sentBytes;
        private static long _sentMessages;
        private static DateTime _sentLastTime;
        private static long _sendDuration;
        private static long _minSendDuration = long.MaxValue;
        private static long _maxSendDuration;
        private static long _failedMessages;
        private static long _failedTime;
        private static BlockingCollection<string> _monitoredItemsDataQueue;
        private static CancellationTokenSource _tokenSource;
        private static Task _monitoredItemsProcessorTask;
        private static DeviceClient _iotHubClient;
    }
}
