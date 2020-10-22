// ------------------------------------------------------------
//  Copyright (c) Microsoft Corporation.  All rights reserved.
//  Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using Microsoft.Azure.Devices.Client;
using Newtonsoft.Json;
using Opc.Ua;
using Opc.Ua.Server;
using OpcPublisher.Configurations;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;

namespace OpcPublisher
{
    public class PublisherNodeManager : CustomNodeManager2
    {
        public PublisherNodeManager(Opc.Ua.Server.IServerInternal server, ApplicationConfiguration configuration)
        : base(server, configuration, "http://microsoft.com/Opc/Publisher/")
        {
            SystemContext.NodeIdFactory = this;
        }

        /// <summary>
        /// Creates the NodeId for the specified node.
        /// </summary>
        public override NodeId New(ISystemContext context, NodeState node)
        {

            if (node is BaseInstanceState instance && instance.Parent != null)
            {

                if (instance.Parent.NodeId.Identifier is string id)
                {
                    return new NodeId(id + "_" + instance.SymbolicName, instance.Parent.NodeId.NamespaceIndex);
                }
            }

            return node.NodeId;
        }

        /// <summary>
        /// Creates a new folder.
        /// </summary>
        private FolderState CreateFolder(NodeState parent, string path, string name)
        {
            FolderState folder = new FolderState(parent)
            {
                SymbolicName = name,
                ReferenceTypeId = ReferenceTypes.Organizes,
                TypeDefinitionId = ObjectTypeIds.FolderType,
                NodeId = new NodeId(path, NamespaceIndex),
                BrowseName = new QualifiedName(path, NamespaceIndex),
                DisplayName = new LocalizedText("en", name),
                WriteMask = AttributeWriteMask.None,
                UserWriteMask = AttributeWriteMask.None,
                EventNotifier = EventNotifiers.None
            };
            parent?.AddChild(folder);

            return folder;
        }


        /// <summary>
        /// Does any initialization required before the address space can be used.
        /// </summary>
        /// <remarks>
        /// The externalReferences is an out parameter that allows the node manager to link to nodes
        /// in other node managers. For example, the 'Objects' node is managed by the CoreNodeManager and
        /// should have a reference to the root folder node(s) exposed by this node manager.  
        /// </remarks>
        public override void CreateAddressSpace(IDictionary<NodeId, IList<IReference>> externalReferences)
        {
            lock (Lock)
            {

                if (!externalReferences.TryGetValue(ObjectIds.ObjectsFolder, out IList<IReference> references))
                {
                    externalReferences[ObjectIds.ObjectsFolder] = references = new List<IReference>();
                }

                FolderState root = CreateFolder(null, "OpcPublisher", "OpcPublisher");
                root.AddReference(ReferenceTypes.Organizes, true, ObjectIds.ObjectsFolder);
                references.Add(new NodeStateReference(ReferenceTypes.Organizes, false, root.NodeId));
                root.EventNotifier = EventNotifiers.SubscribeToEvents;
                AddRootNotifier(root);

                List<BaseDataVariableState> variables = new List<BaseDataVariableState>();

                try
                {
                    FolderState dataFolder = CreateFolder(root, "Data", "Data");

                    const string connectionStringItemName = "ConnectionString";
                    DataItemState item = CreateDataItemVariable(dataFolder, connectionStringItemName, connectionStringItemName, BuiltInType.String, ValueRanks.Scalar, AccessLevels.CurrentWrite);
                    item.Value = String.Empty;

                    FolderState methodsFolder = CreateFolder(root, "Methods", "Methods");

                    MethodState publishNodeMethod = CreateMethod(methodsFolder, "PublishNode", "PublishNode");
                    SetPublishNodeMethodProperties(ref publishNodeMethod);

                    MethodState unpublishNodeMethod = CreateMethod(methodsFolder, "UnpublishNode", "UnpublishNode");
                    SetUnpublishNodeMethodProperties(ref unpublishNodeMethod);

                    MethodState getPublishedNodesLegacyMethod = CreateMethod(methodsFolder, "GetPublishedNodes", "GetPublishedNodes");
                    SetGetPublishedNodesLegacyMethodProperties(ref getPublishedNodesLegacyMethod);
                }
                catch (Exception e)
                {
                    Utils.Trace(e, "Error creating the address space.");
                }

                AddPredefinedNode(SystemContext, root);
            }
        }

        /// <summary>
        /// Sets properties of the PublishNode method.
        /// </summary>
        private void SetPublishNodeMethodProperties(ref MethodState method)
        {
            // define input arguments
            method.InputArguments = new PropertyState<Argument[]>(method)
            {
                NodeId = new NodeId(method.BrowseName.Name + "InArgs", NamespaceIndex),
                BrowseName = BrowseNames.InputArguments
            };
            method.InputArguments.DisplayName = method.InputArguments.BrowseName.Name;
            method.InputArguments.TypeDefinitionId = VariableTypeIds.PropertyType;
            method.InputArguments.ReferenceTypeId = ReferenceTypeIds.HasProperty;
            method.InputArguments.DataType = DataTypeIds.Argument;
            method.InputArguments.ValueRank = ValueRanks.OneDimension;

            method.InputArguments.Value = new Argument[]
            {
                new Argument { Name = "NodeId", Description = "NodeId of the node to publish in NodeId format.",  DataType = DataTypeIds.String, ValueRank = ValueRanks.Scalar },
                new Argument { Name = "EndpointUrl", Description = "Endpoint URI of the OPC UA server owning the node.",  DataType = DataTypeIds.String, ValueRank = ValueRanks.Scalar }
            };

            method.OnCallMethod = new GenericMethodCalledEventHandler(OnPublishNodeCall);
        }

        /// <summary>
        /// Sets properties of the UnpublishNode method.
        /// </summary>
        private void SetUnpublishNodeMethodProperties(ref MethodState method)
        {
            // define input arguments
            method.InputArguments = new PropertyState<Argument[]>(method)
            {
                NodeId = new NodeId(method.BrowseName.Name + "InArgs", NamespaceIndex),
                BrowseName = BrowseNames.InputArguments
            };
            method.InputArguments.DisplayName = method.InputArguments.BrowseName.Name;
            method.InputArguments.TypeDefinitionId = VariableTypeIds.PropertyType;
            method.InputArguments.ReferenceTypeId = ReferenceTypeIds.HasProperty;
            method.InputArguments.DataType = DataTypeIds.Argument;
            method.InputArguments.ValueRank = ValueRanks.OneDimension;

            method.InputArguments.Value = new Argument[]
            {
                new Argument { Name = "NodeId", Description = "NodeId of the node to publish in NodeId format.",  DataType = DataTypeIds.String, ValueRank = ValueRanks.Scalar },
                new Argument { Name = "EndpointUrl", Description = "Endpoint URI of the OPC UA server owning the node.",  DataType = DataTypeIds.String, ValueRank = ValueRanks.Scalar },
            };

            method.OnCallMethod = new GenericMethodCalledEventHandler(OnUnpublishNodeCall);
        }

        /// <summary>
        /// Sets properties of the GetPublishedNodes method, which is only there for backward compatibility.
        /// This method is acutally returning the configured nodes in NodeId syntax.
        /// </summary>
        private void SetGetPublishedNodesLegacyMethodProperties(ref MethodState method)
        {
            // define input arguments
            method.InputArguments = new PropertyState<Argument[]>(method)
            {
                NodeId = new NodeId(method.BrowseName.Name + "InArgs", NamespaceIndex),
                BrowseName = BrowseNames.InputArguments
            };
            method.InputArguments.DisplayName = method.InputArguments.BrowseName.Name;
            method.InputArguments.TypeDefinitionId = VariableTypeIds.PropertyType;
            method.InputArguments.ReferenceTypeId = ReferenceTypeIds.HasProperty;
            method.InputArguments.DataType = DataTypeIds.Argument;
            method.InputArguments.ValueRank = ValueRanks.OneDimension;

            method.InputArguments.Value = new Argument[]
            {
                new Argument { Name = "EndpointUrl", Description = "Endpoint URI of the OPC UA server to return the published nodes for.",  DataType = DataTypeIds.String, ValueRank = ValueRanks.Scalar }
            };

            // set output arguments
            method.OutputArguments = new PropertyState<Argument[]>(method)
            {
                NodeId = new NodeId(method.BrowseName.Name + "OutArgs", NamespaceIndex),
                BrowseName = BrowseNames.OutputArguments
            };
            method.OutputArguments.DisplayName = method.OutputArguments.BrowseName.Name;
            method.OutputArguments.TypeDefinitionId = VariableTypeIds.PropertyType;
            method.OutputArguments.ReferenceTypeId = ReferenceTypeIds.HasProperty;
            method.OutputArguments.DataType = DataTypeIds.Argument;
            method.OutputArguments.ValueRank = ValueRanks.OneDimension;

            method.OutputArguments.Value = new Argument[]
            {
                        new Argument { Name = "Published nodes", Description = "List of the nodes configured to publish in OPC Publisher in NodeId format",  DataType = DataTypeIds.String, ValueRank = ValueRanks.Scalar }
            };
            method.OnCallMethod = new GenericMethodCalledEventHandler(OnGetPublishedNodesLegacyCall);
        }

        /// <summary>
        /// Creates a new variable.
        /// </summary>
        private DataItemState CreateDataItemVariable(NodeState parent, string path, string name, BuiltInType dataType, int valueRank, byte accessLevel)
        {
            DataItemState variable = new DataItemState(parent);
            variable.ValuePrecision = new PropertyState<double>(variable);
            variable.Definition = new PropertyState<string>(variable);

            variable.Create(
                SystemContext,
                null,
                variable.BrowseName,
                null,
                true);

            variable.SymbolicName = name;
            variable.ReferenceTypeId = ReferenceTypes.Organizes;
            variable.NodeId = new NodeId(path, NamespaceIndex);
            variable.BrowseName = new QualifiedName(path, NamespaceIndex);
            variable.DisplayName = new LocalizedText("en", name);
            variable.WriteMask = AttributeWriteMask.None;
            variable.UserWriteMask = AttributeWriteMask.None;
            variable.DataType = (uint)dataType;
            variable.ValueRank = valueRank;
            variable.AccessLevel = accessLevel;
            variable.UserAccessLevel = AccessLevels.CurrentReadOrWrite;
            variable.Historizing = false;
            variable.Value = Opc.Ua.TypeInfo.GetDefaultValue((uint)dataType, valueRank, Server.TypeTree);
            variable.StatusCode = StatusCodes.Good;
            variable.Timestamp = DateTime.UtcNow;

            if (valueRank == ValueRanks.OneDimension)
            {
                variable.ArrayDimensions = new ReadOnlyList<uint>(new List<uint> { 0 });
            }
            else if (valueRank == ValueRanks.TwoDimensions)
            {
                variable.ArrayDimensions = new ReadOnlyList<uint>(new List<uint> { 0, 0 });
            }

            variable.ValuePrecision.Value = 2;
            variable.ValuePrecision.AccessLevel = AccessLevels.CurrentReadOrWrite;
            variable.ValuePrecision.UserAccessLevel = AccessLevels.CurrentReadOrWrite;
            variable.Definition.Value = String.Empty;
            variable.Definition.AccessLevel = AccessLevels.CurrentReadOrWrite;
            variable.Definition.UserAccessLevel = AccessLevels.CurrentReadOrWrite;

            parent?.AddChild(variable);

            return variable;
        }

        /// <summary>
        /// Creates a new variable using type Numeric as NodeId.
        /// </summary>
        private DataItemState CreateDataItemVariable(NodeState parent, uint id, string name, BuiltInType dataType, int valueRank, byte accessLevel)
        {
            DataItemState variable = new DataItemState(parent);
            variable.ValuePrecision = new PropertyState<double>(variable);
            variable.Definition = new PropertyState<string>(variable);

            variable.Create(
                SystemContext,
                null,
                variable.BrowseName,
                null,
                true);

            variable.SymbolicName = name;
            variable.ReferenceTypeId = ReferenceTypes.Organizes;
            variable.NodeId = new NodeId(id, NamespaceIndex);
            variable.BrowseName = new QualifiedName(name, NamespaceIndex);
            variable.DisplayName = new LocalizedText("en", name);
            variable.WriteMask = AttributeWriteMask.None;
            variable.UserWriteMask = AttributeWriteMask.None;
            variable.DataType = (uint)dataType;
            variable.ValueRank = valueRank;
            variable.AccessLevel = accessLevel;
            variable.UserAccessLevel = AccessLevels.CurrentReadOrWrite;
            variable.Historizing = false;
            variable.Value = Opc.Ua.TypeInfo.GetDefaultValue((uint)dataType, valueRank, Server.TypeTree);
            variable.StatusCode = StatusCodes.Good;
            variable.Timestamp = DateTime.UtcNow;

            if (valueRank == ValueRanks.OneDimension)
            {
                variable.ArrayDimensions = new ReadOnlyList<uint>(new List<uint> { 0 });
            }
            else if (valueRank == ValueRanks.TwoDimensions)
            {
                variable.ArrayDimensions = new ReadOnlyList<uint>(new List<uint> { 0, 0 });
            }

            variable.ValuePrecision.Value = 2;
            variable.ValuePrecision.AccessLevel = AccessLevels.CurrentReadOrWrite;
            variable.ValuePrecision.UserAccessLevel = AccessLevels.CurrentReadOrWrite;
            variable.Definition.Value = String.Empty;
            variable.Definition.AccessLevel = AccessLevels.CurrentReadOrWrite;
            variable.Definition.UserAccessLevel = AccessLevels.CurrentReadOrWrite;

            parent?.AddChild(variable);

            return variable;
        }

        /// <summary>
        /// Creates a new variable.
        /// </summary>
        private BaseDataVariableState CreateVariable(NodeState parent, string path, string name, NodeId dataType, int valueRank)
        {
            BaseDataVariableState variable = new BaseDataVariableState(parent)
            {
                SymbolicName = name,
                ReferenceTypeId = ReferenceTypes.Organizes,
                TypeDefinitionId = VariableTypeIds.BaseDataVariableType,
                NodeId = new NodeId(path, NamespaceIndex),
                BrowseName = new QualifiedName(path, NamespaceIndex),
                DisplayName = new LocalizedText("en", name),
                WriteMask = AttributeWriteMask.DisplayName | AttributeWriteMask.Description,
                UserWriteMask = AttributeWriteMask.DisplayName | AttributeWriteMask.Description,
                DataType = dataType,
                ValueRank = valueRank,
                AccessLevel = AccessLevels.CurrentReadOrWrite,
                UserAccessLevel = AccessLevels.CurrentReadOrWrite,
                Historizing = false,
                StatusCode = StatusCodes.Good,
                Timestamp = DateTime.UtcNow
            };

            if (valueRank == ValueRanks.OneDimension)
            {
                variable.ArrayDimensions = new ReadOnlyList<uint>(new List<uint> { 0 });
            }
            else if (valueRank == ValueRanks.TwoDimensions)
            {
                variable.ArrayDimensions = new ReadOnlyList<uint>(new List<uint> { 0, 0 });
            }

            parent?.AddChild(variable);

            return variable;
        }

        /// <summary>
        /// Creates a new method.
        /// </summary>
        private MethodState CreateMethod(NodeState parent, string path, string name)
        {
            MethodState method = new MethodState(parent)
            {
                SymbolicName = name,
                ReferenceTypeId = ReferenceTypeIds.HasComponent,
                NodeId = new NodeId(path, NamespaceIndex),
                BrowseName = new QualifiedName(path, NamespaceIndex),
                DisplayName = new LocalizedText("en", name),
                WriteMask = AttributeWriteMask.None,
                UserWriteMask = AttributeWriteMask.None,
                Executable = true,
                UserExecutable = true
            };

            parent?.AddChild(method);

            return method;
        }

        /// <summary>
        /// Creates a new method using type Numeric for the NodeId.
        /// </summary>
        private MethodState CreateMethod(NodeState parent, uint id, string name)
        {
            MethodState method = new MethodState(parent)
            {
                SymbolicName = name,
                ReferenceTypeId = ReferenceTypeIds.HasComponent,
                NodeId = new NodeId(id, NamespaceIndex),
                BrowseName = new QualifiedName(name, NamespaceIndex),
                DisplayName = new LocalizedText("en", name),
                WriteMask = AttributeWriteMask.None,
                UserWriteMask = AttributeWriteMask.None,
                Executable = true,
                UserExecutable = true
            };

            parent?.AddChild(method);

            return method;
        }

        /// <summary>
        /// Method to start monitoring a node and publish the data to IoTHub. Executes synchronously.
        /// </summary>
        private ServiceResult OnPublishNodeCall(ISystemContext context, MethodState method, IList<object> inputArguments, IList<object> outputArguments)
        {
            string logPrefix = "OnPublishNodeCall:";
            if (string.IsNullOrEmpty(inputArguments[0] as string) || string.IsNullOrEmpty(inputArguments[1] as string))
            {
                Program.Instance.Logger.Error($"{logPrefix} Invalid Arguments when trying to publish a node.");
                return ServiceResult.Create(StatusCodes.BadArgumentsMissing, "Please provide all arguments as strings!");
            }

            HttpStatusCode statusCode = HttpStatusCode.InternalServerError;
            NodeId nodeId = null;
            ExpandedNodeId expandedNodeId = null;
            Uri endpointUri = null;
            bool isNodeIdFormat = true;
            try
            {
                string id = inputArguments[0] as string;
                if (id.Contains("nsu=", StringComparison.InvariantCulture))
                {
                    expandedNodeId = ExpandedNodeId.Parse(id);
                    isNodeIdFormat = false;
                }
                else
                {
                    nodeId = NodeId.Parse(id);
                    isNodeIdFormat = true;
                }
                endpointUri = new Uri(inputArguments[1] as string);
            }
            catch (UriFormatException)
            {
                Program.Instance.Logger.Error($"{logPrefix} The EndpointUrl has an invalid format '{inputArguments[1] as string}'!");
                return ServiceResult.Create(StatusCodes.BadArgumentsMissing, "Please provide a valid OPC UA endpoint URL as second argument!");
            }
            catch (Exception e)
            {
                Program.Instance.Logger.Error(e, $"{logPrefix} The NodeId has an invalid format '{inputArguments[0] as string}'!");
                return ServiceResult.Create(StatusCodes.BadArgumentsMissing, "Please provide a valid OPC UA NodeId in NodeId or ExpandedNodeId format as first argument!");
            }

            // find/create a session to the endpoint URL and start monitoring the node.
            try
            {
                // lock the publishing configuration till we are done
                Program.Instance._nodeConfig.OpcSessionsListSemaphore.Wait();

                if (Program.Instance.ShutdownTokenSource.IsCancellationRequested)
                {
                    return ServiceResult.Create(StatusCodes.BadUnexpectedError, $"Publisher shutdown in progress.");
                }

                // find the session we need to monitor the node
                OpcUaSessionWrapper opcSession = Program.Instance._nodeConfig.OpcSessions.FirstOrDefault(s => s.EndpointUrl.Equals(endpointUri.OriginalString, StringComparison.OrdinalIgnoreCase));

                // add a new session.
                if (opcSession == null)
                {
                    // create new session info.
                    opcSession = new OpcUaSessionWrapper(endpointUri.OriginalString, true, (uint)Program.Instance._application.ApplicationConfiguration.ClientConfiguration.DefaultSessionTimeout, OpcUserSessionAuthenticationMode.Anonymous, null);
                    Program.Instance._nodeConfig.OpcSessions.Add(opcSession);
                    Program.Instance.Logger.Information($"OnPublishNodeCall: No matching session found for endpoint '{endpointUri.OriginalString}'. Requested to create a new one.");
                }

                if (isNodeIdFormat)
                {
                    // add the node info to the subscription with the default publishing interval, execute syncronously
                    Program.Instance.Logger.Debug($"{logPrefix} Request to monitor item with NodeId '{nodeId.ToString()}' (with default PublishingInterval and SamplingInterval)");
                    statusCode = opcSession.AddNodeForMonitoringAsync(nodeId, null, null, null, null, null, null, Program.Instance.ShutdownTokenSource.Token).Result;
                }
                else
                {
                    // add the node info to the subscription with the default publishing interval, execute syncronously
                    Program.Instance.Logger.Debug($"{logPrefix} Request to monitor item with ExpandedNodeId '{expandedNodeId.ToString()}' (with default PublishingInterval and SamplingInterval)");
                    statusCode = opcSession.AddNodeForMonitoringAsync(null, expandedNodeId, null, null, null, null, null, Program.Instance.ShutdownTokenSource.Token).Result;
                }
            }
            catch (Exception e)
            {
                Program.Instance.Logger.Error(e, $"{logPrefix} Exception while trying to configure publishing node '{(isNodeIdFormat ? nodeId.ToString() : expandedNodeId.ToString())}'");
                return ServiceResult.Create(e, StatusCodes.BadUnexpectedError, $"Unexpected error publishing node: {e.Message}");
            }
            finally
            {
                Program.Instance._nodeConfig.OpcSessionsListSemaphore.Release();
            }

            if (statusCode == HttpStatusCode.OK || statusCode == HttpStatusCode.Accepted)
            {
                return ServiceResult.Good;
            }
            return ServiceResult.Create(StatusCodes.Bad, "Can not start monitoring node! Reason unknown.");
        }

        /// <summary>
        /// Method to remove the node from the subscription and stop publishing telemetry to IoTHub. Executes synchronously.
        /// </summary>
        private ServiceResult OnUnpublishNodeCall(ISystemContext context, MethodState method, IList<object> inputArguments, IList<object> outputArguments)
        {
            string logPrefix = "OnUnpublishNodeCall:";
            if (string.IsNullOrEmpty(inputArguments[0] as string) || string.IsNullOrEmpty(inputArguments[1] as string))
            {
                Program.Instance.Logger.Error($"{logPrefix} Invalid arguments!");
                return ServiceResult.Create(StatusCodes.BadArgumentsMissing, "Please provide all arguments!");
            }

            HttpStatusCode statusCode = HttpStatusCode.InternalServerError;
            NodeId nodeId = null;
            ExpandedNodeId expandedNodeId = null;
            Uri endpointUri = null;
            bool isNodeIdFormat = true;
            try
            {
                string id = inputArguments[0] as string;
                if (id.Contains("nsu=", StringComparison.InvariantCulture))
                {
                    expandedNodeId = ExpandedNodeId.Parse(id);
                    isNodeIdFormat = false;
                }
                else
                {
                    nodeId = NodeId.Parse(id);
                    isNodeIdFormat = true;
                }
                endpointUri = new Uri(inputArguments[1] as string);
            }
            catch (UriFormatException)
            {
                Program.Instance.Logger.Error($"{logPrefix} The endpointUrl is invalid '{inputArguments[1] as string}'!");
                return ServiceResult.Create(StatusCodes.BadArgumentsMissing, "Please provide a valid OPC UA endpoint URL as second argument!");
            }
            catch (Exception e)
            {
                Program.Instance.Logger.Error(e, $"{logPrefix} The NodeId has an invalid format '{inputArguments[0] as string}'!");
                return ServiceResult.Create(StatusCodes.BadArgumentsMissing, "Please provide a valid OPC UA NodeId in NodeId or ExpandedNodeId format as first argument!");
            }

            // find the session and stop monitoring the node.
            try
            {
                Program.Instance._nodeConfig.OpcSessionsListSemaphore.Wait();
                if (Program.Instance.ShutdownTokenSource.IsCancellationRequested)
                {
                    return ServiceResult.Create(StatusCodes.BadUnexpectedError, $"Publisher shutdown in progress.");
                }

                // find the session we need to monitor the node
                OpcUaSessionWrapper opcSession = null;
                try
                {
                    opcSession = Program.Instance._nodeConfig.OpcSessions.FirstOrDefault(s => s.EndpointUrl.Equals(endpointUri.OriginalString, StringComparison.OrdinalIgnoreCase));
                }
                catch
                {
                    opcSession = null;
                }

                if (opcSession == null)
                {
                    // do nothing if there is no session for this endpoint.
                    Program.Instance.Logger.Error($"{logPrefix} Session for endpoint '{endpointUri.OriginalString}' not found.");
                    return ServiceResult.Create(StatusCodes.BadSessionIdInvalid, "Session for endpoint of node to unpublished not found!");
                }
                else
                {
                    if (isNodeIdFormat)
                    {
                        // stop monitoring the node, execute syncronously
                        Program.Instance.Logger.Information($"{logPrefix} Request to stop monitoring item with NodeId '{nodeId.ToString()}')");
                        statusCode = opcSession.RequestMonitorItemRemovalAsync(nodeId, null, Program.Instance.ShutdownTokenSource.Token).Result;
                    }
                    else
                    {
                        // stop monitoring the node, execute syncronously
                        Program.Instance.Logger.Information($"{logPrefix} Request to stop monitoring item with ExpandedNodeId '{expandedNodeId.ToString()}')");
                        statusCode = opcSession.RequestMonitorItemRemovalAsync(null, expandedNodeId, Program.Instance.ShutdownTokenSource.Token).Result;
                    }
                }
            }
            catch (Exception e)
            {
                Program.Instance.Logger.Error(e, $"{logPrefix} Exception while trying to configure publishing node '{nodeId.ToString()}'");
                return ServiceResult.Create(e, StatusCodes.BadUnexpectedError, $"Unexpected error unpublishing node: {e.Message}");
            }
            finally
            {
                Program.Instance._nodeConfig.OpcSessionsListSemaphore.Release();
            }
            return statusCode == HttpStatusCode.OK || statusCode == HttpStatusCode.Accepted ? ServiceResult.Good : ServiceResult.Create(StatusCodes.Bad, "Can not stop monitoring node!");
        }

        /// <summary>
        /// Method to get the list of configured nodes and is only there for backward compatibility. Executes synchronously.
        /// The format of the returned node description is using NodeId format. The assumption
        /// is that the caller is able to access the namespace array of the server
        /// on the endpoint URL(s) themselve and do the correct mapping.
        /// </summary>
        private ServiceResult OnGetPublishedNodesLegacyCall(ISystemContext context, MethodState method, IList<object> inputArguments, IList<object> outputArguments)
        {
            string logPrefix = "OnGetPublishedNodesLegacyCall:";
            Uri endpointUri = null;

            if (string.IsNullOrEmpty(inputArguments[0] as string))
            {
                Program.Instance.Logger.Information($"{logPrefix} returning all nodes of all endpoints'!");
            }
            else
            {
                try
                {
                    endpointUri = new Uri(inputArguments[0] as string);
                }
                catch (UriFormatException)
                {
                    Program.Instance.Logger.Error($"{logPrefix} The endpointUrl is invalid '{inputArguments[0] as string}'!");
                    return ServiceResult.Create(StatusCodes.BadArgumentsMissing, "Please provide a valid OPC UA endpoint URL as first argument!");
                }
            }

            // get the list of published nodes in NodeId format
            List<ConfigurationFileEntryLegacyModel> configFileEntries = Program.Instance._nodeConfig.GetPublisherConfigurationFileEntriesAsNodeIdsAsync(endpointUri.OriginalString).Result;
            outputArguments[0] = JsonConvert.SerializeObject(configFileEntries);
            Program.Instance.Logger.Information($"{logPrefix} Success (number of entries: {configFileEntries.Count})");
            return ServiceResult.Good;
        }
    }
}
