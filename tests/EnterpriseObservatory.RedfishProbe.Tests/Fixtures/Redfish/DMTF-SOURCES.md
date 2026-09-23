# Redfish fixtures — provenance

Downloaded from DMTF's published mockup bundles (DSP2043), unmodified except
for `Storage/1/index.json`, which was copied byte-for-byte from its source
file.

| File | Source |
|---|---|
| `power.json` | https://github.com/DMTF/Redfish-Publications/blob/main/mockups/public-rackmount1/Chassis/1U/Power/index.json |
| `thermal.json` | https://github.com/DMTF/Redfish-Publications/blob/main/mockups/public-rackmount1/Chassis/1U/Thermal/index.json |
| `computersystem.json` | https://github.com/DMTF/Redfish-Publications/blob/main/mockups/public-rackmount1/Systems/437XR1138R2/index.json |
| `manager.json` | https://github.com/DMTF/Redfish-Publications/blob/main/mockups/public-rackmount1/Managers/BMC/index.json |
| `logservice.json` | https://github.com/DMTF/Redfish-Publications/blob/main/mockups/public-rackmount1/Systems/437XR1138R2/LogServices/Log1/index.json |
| `logentries.json` | https://github.com/DMTF/Redfish-Publications/blob/main/mockups/public-rackmount1/Systems/437XR1138R2/LogServices/Log1/Entries/index.json |
| `firmware-bmc.json` | https://github.com/DMTF/Redfish-Publications/blob/main/mockups/public-rackmount1/UpdateService/FirmwareInventory/BMC/index.json |
| `firmwareinventory-collection.json` | https://github.com/DMTF/Redfish-Publications/blob/main/mockups/public-rackmount1/UpdateService/FirmwareInventory/index.json |
| `storage.json` | https://github.com/DMTF/Redfish-Publications/blob/main/mockups/public-localstorage/Systems/437XR1138R2/Storage/1/index.json |
| `drive-1.json` | https://github.com/DMTF/Redfish-Publications/blob/main/mockups/public-localstorage/Chassis/1U/Drives/35D38F11ACEF7BD3/index.json |
| `memory-dimm1.json` | https://github.com/DMTF/Redfish-Publications/blob/main/mockups/public-localstorage/Systems/437XR1138R2/Memory/DIMM1/index.json |

`public-rackmount1` does not include a `Storage` collection with `Drives`
(only the deprecated `SimpleStorage`); `public-localstorage` does and was
used for `storage.json`, `drive-1.json` and `memory-dimm1.json` instead. Both
are DMTF-maintained mockups in the same repository, generated from the same
schema set.

These are generic DMTF examples, not HPE iLO responses: `Oem.Hpe.*`
extensions (`AggregateHealthStatus`, `AgentlessManagementService`,
`WearStatus`, `DIMMStatus`, `Severity`, `Repaired`) are absent from them by
construction. `RedfishReport` reports each of those as ABSENT against these
fixtures — that is the correct reading of this sample, not a parser gap. A
live iLO 5/6 is expected to add them (docs/reference-approaches.md §10.7),
and that is exactly what M6.0b's live run will measure.
