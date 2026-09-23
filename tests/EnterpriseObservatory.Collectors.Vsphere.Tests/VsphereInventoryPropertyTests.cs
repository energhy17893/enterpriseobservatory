using EnterpriseObservatory.Collectors.Vsphere;

namespace EnterpriseObservatory.Collectors.Vsphere.Tests;

/// <summary>
/// The properties added for VM sizing, path redundancy, snapshots and thin
/// overcommit, read the way vCenter actually sends them.
/// </summary>
/// <remarks>
/// <para>
/// Against XML rather than against hand-built <c>PropertyObject</c>s, and the
/// distinction is the whole point of this file. A property that parses in a
/// unit test and not against a real response is the failure mode this
/// collector has already met once: <c>volume</c> turned out to be a sibling of
/// <c>mountInfo</c> rather than a child, which no amount of testing the reader
/// in isolation would have caught.
/// </para>
/// <para>
/// These fixtures follow the published vim25 schema. That is weaker evidence
/// than the <c>mountInfo</c> fixture, which was dumped from a live server, and
/// it is recorded here so that nobody later mistakes "the tests pass" for
/// "the paths are confirmed".
/// </para>
/// </remarks>
public class VsphereInventoryPropertyTests
{
    private static readonly Dictionary<string, List<string>> NoVolumes = [];

    private static PropertyObject Single(string xml) =>
        Assert.Single(PropertyCollectorParser.ParsePage(xml).Objects);

    // --- VM sizing --------------------------------------------------------

    private const string SizedVm = """
        <RetrievePropertiesExResponse xmlns="urn:vim25">
          <returnval>
            <objects>
              <obj type="VirtualMachine">vm-441</obj>
              <propSet><name>name</name><val>app-db-01</val></propSet>
              <propSet><name>config.hardware.numCPU</name><val>8</val></propSet>
              <propSet><name>config.hardware.memoryMB</name><val>32768</val></propSet>
              <propSet><name>config.cpuAllocation.limit</name><val>4000</val></propSet>
              <propSet><name>config.memoryAllocation.limit</name><val>-1</val></propSet>
            </objects>
          </returnval>
        </RetrievePropertiesExResponse>
        """;

    [Fact]
    public void The_vCPU_count_is_read_because_ready_time_cannot_be_a_percentage_without_it()
    {
        // cpu.ready.summation is summed across every vCPU, so dividing only by
        // the interval overstates it by exactly this number. An eight-way
        // machine at a genuinely healthy 2% reads as 16%, and somebody is sent
        // to fix a machine that is fine. If this stops being read, the
        // contention rule has no honest figure to state at all.
        var vm = VsphereClient.ToVirtualMachine(Single(SizedVm));

        Assert.Equal(8, vm.VirtualCpuCount);
        Assert.Equal(32768, vm.ConfiguredMemoryMb);
    }

    [Fact]
    public void A_configured_CPU_limit_is_read_so_throttling_is_not_mistaken_for_contention()
    {
        // A limited machine waits exactly like a contended one and the host is
        // perfectly healthy. Without the limit a contention rule confidently
        // blames the wrong thing — which is the whole reason Dynatrace carries
        // a guestCpuLimitReached signal of its own.
        var vm = VsphereClient.ToVirtualMachine(Single(SizedVm));

        Assert.Equal(4000, vm.CpuLimitMhz);
    }

    [Fact]
    public void Unlimited_is_carried_as_the_platform_words_it_rather_than_as_unreadable()
    {
        // vCenter says -1 for "no limit". Folding that into null would make it
        // indistinguishable from "the account may not read the configuration",
        // and those two send somebody to different places: one to the VM's
        // settings, one to a role.
        var vm = VsphereClient.ToVirtualMachine(Single(SizedVm));

        Assert.Equal(-1, vm.MemoryLimitMb);
        Assert.True(vm.CpuLimitMhz is > 0);
    }

    [Fact]
    public void A_machine_whose_sizing_was_not_read_reports_null_rather_than_zero()
    {
        // Zero vCPUs is not a machine, it is an absence of knowledge — and a
        // rule that divided by it would either throw or report infinity. The
        // distinction between "not present" and "we did not ask" is one this
        // collector takes seriously everywhere else.
        var vm = VsphereClient.ToVirtualMachine(Single("""
            <RetrievePropertiesExResponse xmlns="urn:vim25">
              <returnval>
                <objects>
                  <obj type="VirtualMachine">vm-9</obj>
                  <propSet><name>name</name><val>tiny</val></propSet>
                </objects>
              </returnval>
            </RetrievePropertiesExResponse>
            """));

        Assert.Null(vm.VirtualCpuCount);
        Assert.Null(vm.ConfiguredMemoryMb);
        Assert.Null(vm.CpuLimitMhz);
        Assert.Null(vm.MemoryLimitMb);
    }

    [Fact]
    public void A_vCPU_count_that_makes_no_sense_is_refused_rather_than_wrapped()
    {
        // A value too large for an int would wrap to something small and
        // plausible, and the ready percentage computed from it would be wrong
        // in a way nobody could see. Refusing it makes the rule decline to
        // state a figure, which is the honest outcome.
        var vm = VsphereClient.ToVirtualMachine(Single("""
            <RetrievePropertiesExResponse xmlns="urn:vim25">
              <returnval>
                <objects>
                  <obj type="VirtualMachine">vm-9</obj>
                  <propSet><name>config.hardware.numCPU</name><val>4294967296</val></propSet>
                </objects>
              </returnval>
            </RetrievePropertiesExResponse>
            """));

        Assert.Null(vm.VirtualCpuCount);
    }

    // --- path redundancy --------------------------------------------------

    /// <remarks>
    /// The shape of <c>HostMultipathInfo</c>: one <c>lun</c> per device, each
    /// naming its device by a reference whose value is the ScsiLun key, and
    /// carrying one <c>path</c> per route. The second property,
    /// <c>scsiLun</c>, is the only table that holds both that key and the
    /// canonical NAA.
    /// </remarks>
    private const string HostWithPaths = """
        <RetrievePropertiesExResponse xmlns="urn:vim25" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
          <returnval>
            <objects>
              <obj type="HostSystem">host-3615</obj>
              <propSet><name>name</name><val>esx07.corp.local</val></propSet>
              <propSet>
                <name>config.storageDevice.multipathInfo</name>
                <val xsi:type="HostMultipathInfo">
                  <lun>
                    <key>key-vim.host.MultipathInfo.LogicalUnit-0200000000600508b1001cb736</key>
                    <id>0200000000600508b1001cb736</id>
                    <lun type="ScsiLun">key-vim.host.ScsiDisk-0200000000600508b1001cb736</lun>
                    <path>
                      <key>key-vim.host.MultipathInfo.Path-vmhba0:C0:T0:L1</key>
                      <name>vmhba0:C0:T0:L1</name>
                      <pathState>active</pathState>
                      <adapter type="HostFibreChannelHba">key-vim.host.FibreChannelHba-vmhba0</adapter>
                    </path>
                    <path>
                      <key>key-vim.host.MultipathInfo.Path-vmhba1:C0:T0:L1</key>
                      <name>vmhba1:C0:T0:L1</name>
                      <pathState>dead</pathState>
                      <adapter type="HostFibreChannelHba">key-vim.host.FibreChannelHba-vmhba1</adapter>
                    </path>
                  </lun>
                </val>
              </propSet>
              <propSet>
                <name>config.storageDevice.scsiLun</name>
                <val>
                  <ScsiLun xsi:type="HostScsiDisk">
                    <key>key-vim.host.ScsiDisk-0200000000600508b1001cb736</key>
                    <uuid>0200000000600508b1001cb736</uuid>
                    <canonicalName>naa.600508b1001cb7368fc569b9146949ad</canonicalName>
                    <deviceName>/vmfs/devices/disks/naa.600508b1001cb7368fc569b9146949ad</deviceName>
                  </ScsiLun>
                </val>
              </propSet>
            </objects>
          </returnval>
        </RetrievePropertiesExResponse>
        """;

    [Fact]
    public void A_storage_path_is_read_with_the_LUN_it_leads_to()
    {
        // This is the link counter map §5c calls the broken one. A storagePath
        // fault counter names its path vmhba0:C0:T0:L1, which carries a bus, a
        // target and a LUN number but no LUN identity — so a bus reset could be
        // attributed to a host and an HBA and never to the datastore it took
        // down. The NAA here is the same identifier a datastore carries as a
        // StorageDeviceId mark, which is what closes the chain.
        var host = VsphereClient.ToHost(Single(HostWithPaths));

        var active = Assert.Single(host.StoragePaths, p => p.Name == "vmhba0:C0:T0:L1");
        Assert.Equal("naa.600508b1001cb7368fc569b9146949ad", active.StorageDeviceId);
        Assert.Equal("active", active.State);
        Assert.Equal("vmhba0", active.Adapter);
    }

    [Fact]
    public void Every_path_to_one_device_is_kept_so_a_lost_one_can_be_counted()
    {
        // The reason this property was ranked top-three. A device is reached
        // over several paths precisely so that losing one costs nothing, which
        // is why losing one is silent: nothing degrades and no counter moves.
        // Keeping only the first path, or only the working ones, would leave
        // the estate looking exactly as it did before the failure — until the
        // second path dies and a datastore goes down.
        var host = VsphereClient.ToHost(Single(HostWithPaths));

        Assert.Equal(2, host.StoragePaths.Count);
        Assert.Single(host.StoragePaths, p => p.State == "dead");
        Assert.All(host.StoragePaths, p =>
            Assert.Equal("naa.600508b1001cb7368fc569b9146949ad", p.StorageDeviceId));
    }

    [Fact]
    public void A_path_whose_device_cannot_be_named_is_still_reported()
    {
        // Two tables have to agree for a path to carry a LUN name, and when
        // they do not the path is still real. Dropping it would under-count
        // redundancy in exactly the situation where something is already
        // wrong, and "this path is dead" is worth saying even when nobody can
        // yet say which LUN it led to.
        var host = VsphereClient.ToHost(Single("""
            <RetrievePropertiesExResponse xmlns="urn:vim25">
              <returnval>
                <objects>
                  <obj type="HostSystem">host-1</obj>
                  <propSet>
                    <name>config.storageDevice.multipathInfo</name>
                    <val>
                      <lun>
                        <id>0200000000deadbeef</id>
                        <lun type="ScsiLun">key-vim.host.ScsiDisk-0200000000deadbeef</lun>
                        <path><name>vmhba2:C0:T3:L7</name><pathState>dead</pathState></path>
                      </lun>
                    </val>
                  </propSet>
                </objects>
              </returnval>
            </RetrievePropertiesExResponse>
            """));

        var path = Assert.Single(host.StoragePaths);
        Assert.Equal("vmhba2:C0:T3:L7", path.Name);
        Assert.Equal("dead", path.State);
        Assert.Equal(string.Empty, path.StorageDeviceId);
    }

    // --- path transport ---------------------------------------------------

    /// <remarks>
    /// Transport types and child names as a live vCenter returned them (probe
    /// <c>--shapes</c>): 1240 <c>HostFibreChannelTargetTransport</c> carrying
    /// <c>portWorldWideName</c> and <c>nodeWorldWideName</c>, 16
    /// <c>HostSerialAttachedTargetTransport</c> and 2
    /// <c>HostPcieTargetTransport</c> with no children. The iSCSI transport is
    /// from the schema only; that estate had none.
    /// </remarks>
    private static IReadOnlyList<VsphereStoragePath> PathsWithTransport(string transports) =>
        VsphereClient.ToHost(Single($"""
            <RetrievePropertiesExResponse xmlns="urn:vim25" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
              <returnval>
                <objects>
                  <obj type="HostSystem">host-1</obj>
                  <propSet>
                    <name>config.storageDevice.multipathInfo</name>
                    <val xsi:type="HostMultipathInfo">
                      <lun>
                        <id>0200000000deadbeef</id>
                        {transports}
                      </lun>
                    </val>
                  </propSet>
                </objects>
              </returnval>
            </RetrievePropertiesExResponse>
            """)).StoragePaths;

    [Fact]
    public void A_fibre_channel_path_names_its_target_port_wwn_in_colon_hex()
    {
        var path = Assert.Single(PathsWithTransport("""
            <path>
              <name>vmhba2:C0:T0:L1</name><pathState>active</pathState>
              <transport xsi:type="HostFibreChannelTargetTransport">
                <nodeWorldWideName>2305843080538627886</nodeWorldWideName>
                <portWorldWideName>5766297885714947898</portWorldWideName>
              </transport>
            </path>
            """));

        Assert.Equal("HostFibreChannelTargetTransport", path.TransportType);
        Assert.Equal("50:06:01:60:3b:20:1f:3a", path.Target);
    }

    [Fact]
    public void A_wwn_above_two_to_the_63_arrives_negative_and_is_read_unsigned()
    {
        // xsd:long on the wire. A WWN whose top bit is set -- NAA type C,
        // common for virtual ports -- is a negative long, and formatting it
        // signed would give a string that joins to no switch's table.
        var path = Assert.Single(PathsWithTransport("""
            <path>
              <name>vmhba2:C0:T1:L1</name>
              <transport xsi:type="HostFibreChannelTargetTransport">
                <portWorldWideName>-4589038236550955004</portWorldWideName>
              </transport>
            </path>
            """));

        Assert.Equal("c0:50:76:09:a1:b2:00:04", path.Target);
    }

    [Fact]
    public void A_small_wwn_is_zero_padded_to_sixteen_digits()
    {
        var path = Assert.Single(PathsWithTransport("""
            <path>
              <name>vmhba2:C0:T2:L1</name>
              <transport xsi:type="HostFibreChannelTargetTransport">
                <portWorldWideName>255</portWorldWideName>
              </transport>
            </path>
            """));

        Assert.Equal("00:00:00:00:00:00:00:ff", path.Target);
    }

    [Fact]
    public void An_iscsi_path_names_its_target_by_iqn()
    {
        var path = Assert.Single(PathsWithTransport("""
            <path>
              <name>vmhba64:C0:T0:L0</name>
              <transport xsi:type="HostInternetScsiTargetTransport">
                <iScsiName>iqn.2001-05.com.equallogic:0-8a0906-vol1</iScsiName>
                <iScsiAlias>vol1</iScsiAlias>
                <address>10.0.0.5:3260</address>
              </transport>
            </path>
            """));

        Assert.Equal("HostInternetScsiTargetTransport", path.TransportType);
        Assert.Equal("iqn.2001-05.com.equallogic:0-8a0906-vol1", path.Target);
    }

    [Fact]
    public void Transports_that_name_no_port_leave_the_target_null()
    {
        // SAS and PCIe arrive with no children at all; a missing transport or
        // an unparseable WWN is likewise not a target. Never invented.
        var paths = PathsWithTransport("""
            <path><name>vmhba0:C0:T0:L0</name><transport xsi:type="HostSerialAttachedTargetTransport"></transport></path>
            <path><name>vmhba1:C0:T0:L0</name><transport xsi:type="HostPcieTargetTransport"></transport></path>
            <path><name>vmhba2:C0:T0:L0</name></path>
            <path><name>vmhba3:C0:T0:L0</name><transport xsi:type="HostFibreChannelTargetTransport"><portWorldWideName>not-a-number</portWorldWideName></transport></path>
            <path><name>vmhba4:C0:T0:L0</name><transport xsi:type="HostFibreChannelTargetTransport"><portWorldWideName>0</portWorldWideName></transport></path>
            """);

        Assert.Equal(5, paths.Count);
        Assert.All(paths, p => Assert.Null(p.Target));
        Assert.Equal("HostSerialAttachedTargetTransport", paths[0].TransportType);
        Assert.Equal("HostPcieTargetTransport", paths[1].TransportType);
        Assert.Equal(string.Empty, paths[2].TransportType);
    }

    // --- cluster configuration wiring -------------------------------------

    [Fact]
    public void A_cluster_carries_its_ha_settings_groups_and_drs_rules()
    {
        var cluster = VsphereClient.ToCluster(Single("""
            <RetrievePropertiesExResponse xmlns="urn:vim25" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
              <returnval>
                <objects>
                  <obj type="ClusterComputeResource">domain-c7</obj>
                  <propSet><name>name</name><val>prod</val></propSet>
                  <propSet>
                    <name>configurationEx</name>
                    <val xsi:type="ClusterConfigInfoEx">
                      <dasConfig>
                        <enabled>true</enabled>
                        <admissionControlPolicy xsi:type="ClusterFailoverResourcesAdmissionControlPolicy"><x>1</x></admissionControlPolicy>
                      </dasConfig>
                      <drsConfig><enabled>true</enabled></drsConfig>
                      <group xsi:type="ClusterVmGroup"><name>db</name><vm type="VirtualMachine">vm-1</vm></group>
                      <group xsi:type="ClusterHostGroup"><name>lic</name><host type="HostSystem">host-1</host></group>
                      <rule xsi:type="ClusterVmHostRuleInfo">
                        <key>1</key><enabled>true</enabled><name>db-on-lic</name><mandatory>false</mandatory>
                        <userCreated>true</userCreated><vmGroupName>db</vmGroupName><affineHostGroupName>lic</affineHostGroupName>
                      </rule>
                    </val>
                  </propSet>
                </objects>
              </returnval>
            </RetrievePropertiesExResponse>
            """));

        Assert.Equal("true", cluster.HaSettings[ClusterHaSettings.Enabled]);
        Assert.Equal(2, cluster.Groups.Count);
        Assert.Equal("db-on-lic", Assert.Single(cluster.DrsRules).Name);
    }

    [Fact]
    public void A_cluster_without_configurationEx_carries_no_ha_settings_rather_than_defaults()
    {
        var cluster = VsphereClient.ToCluster(Single("""
            <RetrievePropertiesExResponse xmlns="urn:vim25">
              <returnval><objects><obj type="ClusterComputeResource">domain-c7</obj></objects></returnval>
            </RetrievePropertiesExResponse>
            """));

        Assert.Same(ClusterHaSettings.None, cluster.HaSettings);
        Assert.Empty(cluster.Groups);
        Assert.Empty(cluster.DrsRules);
    }

    [Fact]
    public void A_host_whose_path_table_was_not_read_reports_no_paths_rather_than_no_redundancy()
    {
        // An unreadable table and a host that has lost every path look the
        // same in a list of zero paths, and they are opposite facts. The
        // payload's read failures are what tell them apart, exactly as they do
        // for cluster HA — so this must stay empty rather than inventing
        // anything.
        var host = VsphereClient.ToHost(Single("""
            <RetrievePropertiesExResponse xmlns="urn:vim25">
              <returnval>
                <objects><obj type="HostSystem">host-1</obj></objects>
              </returnval>
            </RetrievePropertiesExResponse>
            """));

        Assert.Empty(host.StoragePaths);
    }

    // --- snapshots --------------------------------------------------------

    /// <remarks>
    /// A branching chain: one root with two children, one of which has a child
    /// of its own. The recursion is the part worth testing — a reader that
    /// stopped at the roots would report a machine with five snapshots as
    /// having one.
    /// </remarks>
    private const string VmWithSnapshots = """
        <RetrievePropertiesExResponse xmlns="urn:vim25" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
          <returnval>
            <objects>
              <obj type="VirtualMachine">vm-88</obj>
              <propSet><name>name</name><val>fileserver</val></propSet>
              <propSet>
                <name>snapshot</name>
                <val xsi:type="VirtualMachineSnapshotInfo">
                  <currentSnapshot type="VirtualMachineSnapshot">snapshot-2043</currentSnapshot>
                  <rootSnapshotList>
                    <snapshot type="VirtualMachineSnapshot">snapshot-2041</snapshot>
                    <vm type="VirtualMachine">vm-88</vm>
                    <name>before upgrade</name>
                    <id>1</id>
                    <createTime>2026-03-02T21:14:07.412Z</createTime>
                    <state>poweredOn</state>
                    <quiesced>false</quiesced>
                    <childSnapshotList>
                      <snapshot type="VirtualMachineSnapshot">snapshot-2042</snapshot>
                      <name>after patch</name>
                      <id>2</id>
                      <createTime>2026-08-19T08:00:00Z</createTime>
                      <childSnapshotList>
                        <snapshot type="VirtualMachineSnapshot">snapshot-2043</snapshot>
                        <name>just in case</name>
                        <id>3</id>
                        <createTime>2026-09-17T11:30:00Z</createTime>
                      </childSnapshotList>
                    </childSnapshotList>
                  </rootSnapshotList>
                </val>
              </propSet>
            </objects>
          </returnval>
        </RetrievePropertiesExResponse>
        """;

    [Fact]
    public void A_branching_snapshot_tree_is_read_all_the_way_down()
    {
        // The property parser builds a tree rather than a flat field map — it
        // had to, for the volume/extent nesting — so the recursion is
        // renderable. A reader that took only the roots would report the
        // machine below as carrying one snapshot when it carries three, and
        // the two it lost are the newer ones somebody would actually delete.
        var vm = VsphereClient.ToVirtualMachine(Single(VmWithSnapshots));

        Assert.Equal(3, vm.Snapshots.Count);
        Assert.Equal(["snapshot-2041", "snapshot-2042", "snapshot-2043"],
            vm.Snapshots.Select(s => s.MoRef));
        Assert.Equal([1, 2, 3], vm.Snapshots.Select(s => s.Depth));
    }

    [Fact]
    public void Snapshots_come_back_oldest_first_because_age_is_what_the_alert_is_about()
    {
        // "How long has this been here" is the question, and an ordering that
        // put the newest first would have every caller re-sort or, worse,
        // report the age of the most recent one — which is always small and
        // always reassuring.
        var vm = VsphereClient.ToVirtualMachine(Single(VmWithSnapshots));

        Assert.Equal("before upgrade", vm.Snapshots[0].Name);
        Assert.Equal(
            new DateTimeOffset(2026, 3, 2, 21, 14, 7, 412, TimeSpan.Zero),
            vm.Snapshots[0].CreatedAtUtc);
    }

    [Fact]
    public void A_snapshot_with_an_unreadable_creation_time_does_not_become_the_oldest()
    {
        // Sorting a null first would make every machine with one unparseable
        // timestamp look like it had been carrying a snapshot since the epoch,
        // and the estate's most urgent alert would be about nothing.
        var vm = VsphereClient.ToVirtualMachine(Single("""
            <RetrievePropertiesExResponse xmlns="urn:vim25">
              <returnval>
                <objects>
                  <obj type="VirtualMachine">vm-5</obj>
                  <propSet>
                    <name>snapshot</name>
                    <val>
                      <rootSnapshotList>
                        <snapshot type="VirtualMachineSnapshot">snapshot-1</snapshot>
                        <name>undated</name>
                        <createTime>not a time</createTime>
                        <childSnapshotList>
                          <snapshot type="VirtualMachineSnapshot">snapshot-2</snapshot>
                          <name>dated</name>
                          <createTime>2026-09-01T00:00:00Z</createTime>
                        </childSnapshotList>
                      </rootSnapshotList>
                    </val>
                  </propSet>
                </objects>
              </returnval>
            </RetrievePropertiesExResponse>
            """));

        Assert.Equal("dated", vm.Snapshots[0].Name);
        Assert.Null(vm.Snapshots[1].CreatedAtUtc);
    }

    /// <remarks>
    /// <c>layoutEx</c> as vCenter reports it for a machine with one disk and
    /// one snapshot: a base extent, a delta extent, and the snapshot's own
    /// memory image. The disk's chain has two links, the first being the base.
    /// </remarks>
    private const string VmWithLayout = """
        <RetrievePropertiesExResponse xmlns="urn:vim25">
          <returnval>
            <objects>
              <obj type="VirtualMachine">vm-88</obj>
              <propSet>
                <name>layoutEx.file</name>
                <val>
                  <VirtualMachineFileLayoutExFileInfo>
                    <key>0</key><name>[vmfs01] fs/fs.vmx</name><type>config</type>
                    <size>3245</size><uniqueSize>3245</uniqueSize>
                  </VirtualMachineFileLayoutExFileInfo>
                  <VirtualMachineFileLayoutExFileInfo>
                    <key>1</key><name>[vmfs01] fs/fs-flat.vmdk</name><type>diskExtent</type>
                    <size>107374182400</size><uniqueSize>107374182400</uniqueSize>
                  </VirtualMachineFileLayoutExFileInfo>
                  <VirtualMachineFileLayoutExFileInfo>
                    <key>2</key><name>[vmfs01] fs/fs-000001-delta.vmdk</name><type>diskExtent</type>
                    <size>42949672960</size><uniqueSize>42949672960</uniqueSize>
                  </VirtualMachineFileLayoutExFileInfo>
                  <VirtualMachineFileLayoutExFileInfo>
                    <key>3</key><name>[vmfs01] fs/fs-Snapshot1.vmsn</name><type>snapshotMemory</type>
                    <size>8589934592</size><uniqueSize>8589934592</uniqueSize>
                  </VirtualMachineFileLayoutExFileInfo>
                </val>
              </propSet>
              <propSet>
                <name>layoutEx.disk</name>
                <val>
                  <VirtualMachineFileLayoutExDiskLayout>
                    <key>2000</key>
                    <chain><fileKey>1</fileKey></chain>
                    <chain><fileKey>2</fileKey></chain>
                  </VirtualMachineFileLayoutExDiskLayout>
                </val>
              </propSet>
            </objects>
          </returnval>
        </RetrievePropertiesExResponse>
        """;

    [Fact]
    public void Snapshot_size_counts_the_delta_disks_and_not_the_base_disk()
    {
        // The whole difficulty of this property. A delta disk is labelled
        // diskExtent exactly like the base disk it hangs off, and nothing in
        // the file entry tells them apart — only the disk's chain does, its
        // first link being the base and every later one a snapshot level.
        // Counting the base would report every machine in the estate as
        // carrying a snapshot the size of itself; counting only the .vmsn
        // would report 8 GB where 48 are at stake.
        var vm = VsphereClient.ToVirtualMachine(Single(VmWithLayout));

        Assert.Equal(42949672960L + 8589934592L, vm.SnapshotBytes);
    }

    [Fact]
    public void A_machine_whose_layout_was_not_read_reports_null_rather_than_zero()
    {
        // Zero looks like a measurement. The counter map calls that this
        // product's most dangerous number: an operator seeing "0 GB of
        // snapshots" eliminates snapshots as a cause, when in fact nobody
        // looked.
        var vm = VsphereClient.ToVirtualMachine(Single("""
            <RetrievePropertiesExResponse xmlns="urn:vim25">
              <returnval>
                <objects><obj type="VirtualMachine">vm-1</obj></objects>
              </returnval>
            </RetrievePropertiesExResponse>
            """));

        Assert.Null(vm.SnapshotBytes);
    }

    [Fact]
    public void A_machine_with_no_snapshots_reports_a_measured_zero()
    {
        // The other side of the same distinction: the layout was read, it has
        // one link per disk and nothing labelled snapshot, so zero here is an
        // answer rather than an absence.
        var vm = VsphereClient.ToVirtualMachine(Single("""
            <RetrievePropertiesExResponse xmlns="urn:vim25">
              <returnval>
                <objects>
                  <obj type="VirtualMachine">vm-1</obj>
                  <propSet>
                    <name>layoutEx.file</name>
                    <val>
                      <VirtualMachineFileLayoutExFileInfo>
                        <key>1</key><type>diskExtent</type><size>107374182400</size>
                      </VirtualMachineFileLayoutExFileInfo>
                    </val>
                  </propSet>
                  <propSet>
                    <name>layoutEx.disk</name>
                    <val>
                      <VirtualMachineFileLayoutExDiskLayout>
                        <key>2000</key><chain><fileKey>1</fileKey></chain>
                      </VirtualMachineFileLayoutExDiskLayout>
                    </val>
                  </propSet>
                </objects>
              </returnval>
            </RetrievePropertiesExResponse>
            """));

        Assert.Equal(0L, vm.SnapshotBytes);
        Assert.Empty(vm.Snapshots);
    }

    // --- thin overcommit --------------------------------------------------

    [Fact]
    public void Uncommitted_space_is_read_because_fullness_only_says_whether_a_datastore_has_filled()
    {
        // Counter map §4: the only measure that warns long before a datastore
        // fills. Fullness is a statement about today — at 84% nothing is said
        // at all — while this says what has already been promised, which is
        // what decides whether the volume is going to fill.
        var datastore = VsphereClient.ToDatastore(
            Single("""
                <RetrievePropertiesExResponse xmlns="urn:vim25">
                  <returnval>
                    <objects>
                      <obj type="Datastore">datastore-41</obj>
                      <propSet><name>name</name><val>vmfs01</val></propSet>
                      <propSet><name>summary.capacity</name><val>10995116277760</val></propSet>
                      <propSet><name>summary.freeSpace</name><val>2199023255552</val></propSet>
                      <propSet><name>summary.uncommitted</name><val>6597069766656</val></propSet>
                    </objects>
                  </returnval>
                </RetrievePropertiesExResponse>
                """),
            volumeDevices: NoVolumes);

        Assert.Equal(6597069766656L, datastore.UncommittedBytes);
    }

    [Fact]
    public void A_datastore_that_reported_no_uncommitted_space_is_null_rather_than_zero()
    {
        // The property is legitimately absent on a datastore with no thin
        // provisioning, and absent when the account could not read it. Writing
        // zero would assert that nothing has been promised, which is a claim
        // rather than a reading.
        var datastore = VsphereClient.ToDatastore(
            Single("""
                <RetrievePropertiesExResponse xmlns="urn:vim25">
                  <returnval>
                    <objects>
                      <obj type="Datastore">datastore-41</obj>
                      <propSet><name>name</name><val>vmfs01</val></propSet>
                    </objects>
                  </returnval>
                </RetrievePropertiesExResponse>
                """),
            volumeDevices: NoVolumes);

        Assert.Null(datastore.UncommittedBytes);
    }

    // --- the request itself -----------------------------------------------

    [Fact]
    public void Every_property_these_tests_depend_on_is_one_the_collector_asks_for()
    {
        // A wrong or forgotten path is a silently missing value rather than an
        // error: vCenter simply does not return it and the reading is null
        // forever. These tests prove the parsing, so this one proves the
        // asking — otherwise a property could be perfectly readable and never
        // requested, which is the same outcome with a longer explanation.
        var host = VsphereClient.InventoryPropertiesFor("HostSystem");
        var vm = VsphereClient.InventoryPropertiesFor("VirtualMachine");
        var datastore = VsphereClient.InventoryPropertiesFor("Datastore");

        Assert.Contains("config.storageDevice.multipathInfo", host);
        Assert.Contains("config.storageDevice.scsiLun", host);

        Assert.Contains("config.hardware.numCPU", vm);
        Assert.Contains("config.hardware.memoryMB", vm);
        Assert.Contains("config.cpuAllocation.limit", vm);
        Assert.Contains("config.memoryAllocation.limit", vm);
        Assert.Contains("snapshot", vm);
        Assert.Contains("layoutEx.file", vm);
        Assert.Contains("layoutEx.disk", vm);

        Assert.Contains("summary.uncommitted", datastore);
    }

    [Fact]
    public void The_storage_summary_is_asked_for_as_fields_rather_than_whole()
    {
        // summary.storage is a structure whose children are all scalars, so
        // the property reader treats it as a value and joins committed,
        // uncommitted and unshared into one string with a separator between
        // them. That is the documented hazard — a structure coming back
        // looking like a value — and the reason snapshot size is taken from
        // layoutEx instead.
        Assert.DoesNotContain("summary.storage", VsphereClient.InventoryPropertiesFor("VirtualMachine"));
    }
}
