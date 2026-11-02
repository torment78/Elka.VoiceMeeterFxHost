namespace Elka.VoiceMeeterFxHost.App;

public partial class MainWindow
{
    internal void CheckQuickConnectionChains()
    {
        var savedNodes = _settings.PluginNodes;
        var savedGroups = _settings.PluginGroups;
        var savedCables = _settings.CanvasConnections;
        var savedMode = _vstCanvasMode;
        var savedRouteView = _vstInputCanvasRouteView;
        var savedView = _workspaceView;
        var startChecks = _interactionChecks;
        try
        {
            foreach (var mode in new[] { CallbackMode.Input, CallbackMode.Output, CallbackMode.Main })
            {
                _workspaceView = WorkspaceView.Vst;
                _vstCanvasMode = mode == CallbackMode.Main ? CallbackMode.Input : mode;
                _vstInputCanvasRouteView = mode == CallbackMode.Main
                    ? VstInputCanvasRouteView.DirectOutput : VstInputCanvasRouteView.InputReturn;
                var sourceMode = mode == CallbackMode.Main ? CallbackMode.Input : mode;
                var destinationMode = mode == CallbackMode.Main ? CallbackMode.Output : mode;
                var source = VoicemeeterIoLayout.GetEndpoints(sourceMode, _kind).First();
                var destination = VoicemeeterIoLayout.GetEndpoints(destinationMode, _kind).First();
                var a = new PluginNodeSnapshot { Slot = 12, InstanceId = 1, Name = "Group first", Mode = mode, MissingPlugin = true };
                var b = new PluginNodeSnapshot { Slot = 3, InstanceId = 2, Name = "Group last", Mode = mode, MissingPlugin = true };
                var inserted = new PluginNodeSnapshot { Slot = 7, InstanceId = 3, Name = "Inserted VST", Mode = mode,
                    MainInputPins = 2, SidechainInputPins = 2, InputPins = 4, MissingPlugin = true };
                var next = new PluginNodeSnapshot { Slot = 1, InstanceId = 4, Name = "Next VST", Mode = mode, MissingPlugin = true };
                _settings.PluginNodes = [a, b, inserted, next];
                _settings.PluginGroups = [];
                _settings.CanvasConnections = [];
                var group = new PluginGroupSnapshot { Name = "Existing chain", Mode = mode };
                PluginGroupRouting.AddMembers(group, [a, b], _settings.PluginNodes,
                    _settings.PluginGroups, _settings.CanvasConnections, appendChain: true);
                ConnectQuickGroupSource(group, new QuickGroupEndpoint(sourceMode, source, false));
                var preserved = _settings.CanvasConnections.ToArray();

                void Reset()
                {
                    ClearQuickGroupChain(log: false);
                    _settings.CanvasConnections = preserved.ToList();
                }
                void Finish()
                {
                    Assert(CompleteQuickGroupChainAsync().GetAwaiter().GetResult(), "Enter handles quick selection");
                    Assert(preserved.All(edge => _settings.CanvasConnections.Contains(edge)), "Pre-existing group mappings and upstream cables retain identity");
                    Assert(group.MemberSlots.SequenceEqual(new[] { 12, 3 }) && _settings.PluginGroups.Count == 1,
                        "Quick wiring neither regroups nor reorders the existing chain");
                    Assert(_quickOrderedItems.Count == 0, "Completing the command clears the combined click order");
                }
                void SelectDestination() => AddQuickGroupEndpoint(new EndpointDragInfo(destinationMode, destination, true));

                Reset();
                AddQuickGroupExistingGroup(group);
                AddQuickGroupNode(inserted);
                SelectDestination();
                Finish();
                Assert(_settings.CanvasConnections.Count(edge => edge.Kind == ConnectionGroupOutputToNode &&
                    edge.FromGroupId == group.Id && edge.ToSlot == inserted.Slot && edge.ToPin == edge.FromPin + 2) == 2,
                    "Group -> inserted VST connects both main inputs, not its sidechain");
                Assert(_settings.CanvasConnections.Count(edge => edge.Kind == ConnectionNodeToEndpoint && edge.FromSlot == inserted.Slot) == 2,
                    "Inserted VST -> destination completes the stereo chain");
                Assert(!_settings.CanvasConnections.Any(edge => edge.Kind == ConnectionGroupOutputToEndpoint),
                    "No direct group -> output bypass cable is added");
                var completed = _settings.CanvasConnections.ToArray();
                AddQuickGroupExistingGroup(group);
                AddQuickGroupNode(inserted);
                SelectDestination();
                Finish();
                Assert(completed.SequenceEqual(_settings.CanvasConnections), "Repeating Ctrl-click/Enter preserves existing cables without duplicates");
                Assert(!CompleteQuickGroupChainAsync().GetAwaiter().GetResult() && completed.SequenceEqual(_settings.CanvasConnections),
                    "An extra Enter with no selection does not change cables");
                Assert(ConnectQuickNodeToDestinations(inserted, [new QuickGroupEndpoint(destinationMode, destination, true)]) == 0,
                    "Already-connected endpoints are not reactivated or counted as new cables");

                Reset();
                AddQuickGroupNode(inserted);
                AddQuickGroupNode(next);
                SelectDestination();
                Finish();
                Assert(_settings.CanvasConnections.Count(edge => edge.Kind == ConnectionNodeToNode &&
                    edge.FromSlot == inserted.Slot && edge.ToSlot == next.Slot) == 2,
                    "VST -> VST connects in click order, not slot or creation order");
                Assert(_settings.CanvasConnections.Count(edge => edge.Kind == ConnectionNodeToEndpoint && edge.FromSlot == next.Slot) == 2,
                    "Only the selected chain tail connects to the endpoint");

                Reset();
                AddQuickGroupNode(inserted);
                AddQuickGroupExistingGroup(group);
                SelectDestination();
                Finish();
                Assert(_settings.CanvasConnections.Count(edge => edge.Kind == ConnectionNodeToGroupInput && edge.FromSlot == inserted.Slot) == 2,
                    "Reverse mixed order connects VST -> group");
                Assert(_settings.CanvasConnections.Count(edge => edge.Kind == ConnectionGroupOutputToEndpoint && edge.FromGroupId == group.Id) == 2,
                    "Reverse mixed order connects group -> output");

                Reset();
                AddQuickGroupExistingGroup(group);
                AddQuickGroupNode(inserted);
                AddQuickGroupNode(next);
                SelectDestination();
                Finish();
                Assert(_settings.CanvasConnections.Count(edge => edge.Kind == ConnectionGroupOutputToNode && edge.ToSlot == inserted.Slot) == 2 &&
                    _settings.CanvasConnections.Count(edge => edge.Kind == ConnectionNodeToNode && edge.FromSlot == inserted.Slot && edge.ToSlot == next.Slot) == 2,
                    "Every consecutive pair in a longer mixed chain is connected");

                Reset();
                AddQuickGroupEndpoint(new EndpointDragInfo(sourceMode, source, false));
                AddQuickGroupNode(inserted);
                AddQuickGroupNode(next);
                Finish();
                Assert(_settings.CanvasConnections.Count(edge => edge.Kind == ConnectionEndpointToNode && edge.ToSlot == inserted.Slot) == 2 &&
                    _settings.CanvasConnections.Count(edge => edge.Kind == ConnectionNodeToNode && edge.FromSlot == inserted.Slot && edge.ToSlot == next.Slot) == 2,
                    "Source-only quick selection also connects all selected VSTs");
                Assert(ConnectQuickSourcesToNode(inserted, [new QuickGroupEndpoint(sourceMode, source, false)]) == 0,
                    "Already-connected sources are not reactivated or counted as new cables");

                Reset();
                AddQuickGroupExistingGroup(group);
                AddQuickGroupNode(inserted);
                AddQuickGroupNode(next);
                AddQuickGroupNode(inserted);
                SelectDestination();
                Finish();
                Assert(_settings.CanvasConnections.Count(edge => edge.Kind == ConnectionGroupOutputToNode && edge.ToSlot == next.Slot) == 2 &&
                    _settings.CanvasConnections.Count(edge => edge.Kind == ConnectionNodeToNode && edge.FromSlot == next.Slot && edge.ToSlot == inserted.Slot) == 2,
                    "Re-selecting a VST moves it to the end of the combined click order");

                Reset();
                AddQuickGroupNode(inserted);
                AddQuickGroupNode(next);
                Assert(!ShouldCompleteQuickOrderedConnections() && ShouldCompleteUnattachedQuickGroup(),
                    "Unattached create-group shortcut remains available");
                AddQuickGroupEndpoint(new EndpointDragInfo(sourceMode, source, false));
                SelectDestination();
                Assert(!ShouldCompleteQuickOrderedConnections() && !ShouldCompleteQuickDirectConnections(),
                    "Source + VSTs + destination retains the existing quick create-group flow");

                Reset();
                AddQuickGroupExistingGroup(group);
                AddQuickGroupNode(inserted);
                _quickGroupPluginChoices.Add(new PluginChoice(0, "Browser choice", "VST3"));
                Assert(!ShouldCompleteQuickOrderedConnections(), "Plugin-list selection retains the group-loading flow");
            }
            Console.WriteLine($"PASS: {_interactionChecks - startChecks} ordered quick-connect checks. No windows shown or native audio calls.");
        }
        finally
        {
            ClearQuickGroupChain(log: false);
            _settings.PluginNodes = savedNodes;
            _settings.PluginGroups = savedGroups;
            _settings.CanvasConnections = savedCables;
            _vstCanvasMode = savedMode;
            _vstInputCanvasRouteView = savedRouteView;
            _workspaceView = savedView;
            _selectedPluginNodeSlot = null;
            _selectedPluginGroupId = null;
            RebuildRoutingCanvas();
        }
    }
}
