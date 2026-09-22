# Toplama PR'ı 1 — canlı şekil ölçümü (22 Eylül 2026)

`probe --from-store --candidates`, salt-okunur servis hesabı, canlı vCenter
(10 host, 145 VM, 3 küme, 41 datastore). Her aday yol **tek başına** okundu:
koleksiyon listesinde tek bir geçersiz yol bütün envanter okumasını
düşürdüğü için (InvalidProperty), burada düşen bir aday yalnız kendi okumasını
kaybeder. Çıktı yalnızca ad, tip ve sayı taşır; hiçbir değer basılmaz.

## Karar tablosu

| Yol / çağrı | Görüldü | Şekil | Hata | Maliyet (bu estate) | Listeye |
|---|---|---|---|---|---|
| `HostSystem.configIssue` | 10/10 | boş dizi (değer) | — | 1,8 KB | **evet** |
| `VirtualMachine.configIssue` | 145/145 | boş dizi | — | 21 KB | **evet** |
| `ClusterComputeResource.configIssue` | 3/3 | boş dizi | — | 0,9 KB | **evet** |
| `Datastore.configIssue` | 41/41 | boş dizi | — | 6 KB | **evet** |
| `HostSystem.runtime.healthSystemRuntime` | 10/10 | yapı: `systemHealthInfo.numericSensorInfo` ×749 (9 alt alan, `healthState` dahil), `hardwareStatusInfo.{cpu,memory}StatusInfo` ×44/×60 (`name`, `status`); `storageStatusInfo` bu estate'te **yok** | — | 343 KB, 0,4 sn | **evet** — `numericSensorInfo` canlıda görüldü |
| `VirtualMachine.runtime.connectionState` | 145/145 | değer | — | 26 KB | **evet** |
| `Datastore.summary.maintenanceMode` | 41/41 | değer | — | 7 KB | **evet** |
| `VirtualMachine.runtime.consolidationNeeded` | 145/145 | değer | — | 24 KB | **evet** |
| `VirtualMachine.config.hardware.device` | 145/145 | yapı, 19 cihaz tipi; `VirtualCdrom` ×130 (`backing` 3 tip, `connectable.connected`) | — | **1,19 MB, 0,8 sn** (~8 KB/VM) | **evet** — daha dar yol yok; en pahalı ek |
| `Datastore.host` | 41/41 | yapı: `DatastoreHostMount` ×302 (`key`, `mountInfo.{mounted,accessible,accessMode,path,inaccessibleReason}`) | — | 99 KB | **evet** |
| `HostSystem.summary.currentEVCModeKey` | 2/10 | değer (EVC kapalıyken yok) | — | 1 KB | hayır — kümede okunuyor |
| `HostSystem.summary.maxEVCModeKey` | 10/10 | değer | — | 2 KB | hayır — M8.4 kümenin EVC'sini soruyor |
| `ClusterComputeResource.summary.currentEVCModeKey` | — | — | **InvalidProperty** | — | **hayır** — `summary` `ComputeResourceSummary` olarak bildirilmiş |
| `ClusterComputeResource.summary` (bütün) | 3/3 | yapı; `currentEVCModeKey` 1/3 (EVC kapalıyken yok) | — | 10 KB | **evet** — `configurationEx` gibi bütün istenir |
| `HostSystem.config.certificate` | 10/10 | bayt dizisi; 10/10 X.509 olarak çözülüyor, `notAfter` okunuyor | — | **548 KB** (~55 KB/host) | **evet** — ESXi sertifikası için tek okunabilir kaynak |
| `HostSystem.configManager.certificateManager` → `HostCertificateManager.certificateInfo` | — | — | **NoPermission** ×10 | — | **hayır** |
| `LicenseManager.licenses` | — | — | **NoPermission** | — | **hayır** — lisans bitişi salt-okunur rolle okunamıyor |
| vCenter sertifikası (TLS el sıkışması) | evet | sertifika geldi, `notAfter` okunuyor | — | tek bağlantı | toplanmadı (vim25 dışı; M8.7 kuralıyla gelir) |
| `ProfileComplianceManager.QueryComplianceStatus` (filtresiz) | — | — | **InvalidName: profile** | 28 ms | — |
| `QueryComplianceStatus` (entity = 10 host) | evet | **0 sonuç** (host profili bağlı değil) | — | 408 karakter, 27 ms | **hayır** — maliyet küçük, ama sonuç şekli bu estate'te görülemiyor |

## M8.9 — appliance REST, aynı salt-okunur hesap

| Çağrı | HTTP |
|---|---|
| `POST /api/session` | 201 (oturum açıldı) |
| `GET /api/appliance/health/system` | **403** |
| `GET /api/appliance/recovery/backup/job` | **403** |
| `DELETE /api/session` | 204 |

Oturum açılabiliyor, appliance uçları salt-okunur rolle **okunamıyor**.
§10.5'teki topluluk iddiası canlıda doğrulandı: M8.9 bu hesapla yapılamaz.

## Maliyet notu

Eklenen yolların toplamı bu estate'te bir envanter turuna ~2,2 MB ekliyor;
bunun 1,19 MB'ı cihaz listesi, 548 KB'ı host sertifikası, 343 KB'ı sağlık
sensörleri. 2000 VM'lik bir estate'te cihaz listesi tur başına ~16 MB olur —
sayfalı (sayfa başına ~100 nesne ≈ 0,8 MB), 64 MB yanıt sınırının çok altında.

## Ham çıktı

```

=== Candidate property paths (each read alone; names and counts only) ===

  HostSystem.configIssue                             SEEN   objects 10, value 10 (non-empty 0), structure 0, missing 0 , absent 0
                                                            pages 1, reply 1769 chars, 63 ms

  VirtualMachine.configIssue                         SEEN   objects 145, value 145 (non-empty 0), structure 0, missing 0 , absent 0
                                                            pages 2, reply 20959 chars, 132 ms

  ClusterComputeResource.configIssue                 SEEN   objects 3, value 3 (non-empty 0), structure 0, missing 0 , absent 0
                                                            pages 1, reply 878 chars, 52 ms

  Datastore.configIssue                              SEEN   objects 41, value 41 (non-empty 0), structure 0, missing 0 , absent 0
                                                            pages 1, reply 6152 chars, 133 ms

  HostSystem.runtime.healthSystemRuntime             SEEN   objects 10, value 0 (non-empty 0), structure 10, missing 0 , absent 0
                                                            pages 1, reply 343061 chars, 417 ms
      hardwareStatusInfo x10
        cpuStatusInfo x44
          name x44
          status x44
        memoryStatusInfo x60
          name x60
          status x60
      systemHealthInfo x10
        numericSensorInfo x749
          baseUnits x749
          currentReading x749
          healthState x749
          id x749
          name x749
          rateUnits x749
          sensorType x749
          timeStamp x749
          unitModifier x749

  VirtualMachine.runtime.connectionState             SEEN   objects 145, value 145 (non-empty 145), structure 0, missing 0 , absent 0
                                                            pages 2, reply 26469 chars, 108 ms

  Datastore.summary.maintenanceMode                  SEEN   objects 41, value 41 (non-empty 41), structure 0, missing 0 , absent 0
                                                            pages 1, reply 6808 chars, 49 ms

  VirtualMachine.runtime.consolidationNeeded         SEEN   objects 145, value 145 (non-empty 145), structure 0, missing 0 , absent 0
                                                            pages 2, reply 23859 chars, 88 ms

  VirtualMachine.config.hardware.device              SEEN   objects 145, value 0 (non-empty 0), structure 145, missing 0 , absent 0
                                                            pages 2, reply 1186731 chars, 922 ms
      VirtualDevice <ParaVirtualSCSIController> x47
      VirtualDevice <VirtualAHCIController> x119
      VirtualDevice <VirtualCdrom> x130
      VirtualDevice <VirtualDisk> x341
      VirtualDevice <VirtualE1000e> x17
      VirtualDevice <VirtualFloppy> x4
      VirtualDevice <VirtualIDEController> x290
      VirtualDevice <VirtualKeyboard> x145
      VirtualDevice <VirtualLsiLogicController> x63
      VirtualDevice <VirtualLsiLogicSASController> x89
      VirtualDevice <VirtualMachineVMCIDevice> x145
      VirtualDevice <VirtualMachineVideoCard> x145
      VirtualDevice <VirtualPCIController> x145
      VirtualDevice <VirtualPS2Controller> x145
      VirtualDevice <VirtualPointingDevice> x145
      VirtualDevice <VirtualSIOController> x145
      VirtualDevice <VirtualSerialPort> x1
      VirtualDevice <VirtualUSBXHCIController> x74
      VirtualDevice <VirtualVmxnet3> x131

  Datastore.host                                     SEEN   objects 41, value 0 (non-empty 0), structure 41, missing 0 , absent 0
                                                            pages 1, reply 98694 chars, 89 ms
      DatastoreHostMount <DatastoreHostMount> x302
        key <HostSystem> x302
        mountInfo x302
          accessMode x302
          accessible x302
          inaccessibleReason x196
          mounted x302
          path x302

  HostSystem.summary.currentEVCModeKey               SEEN   objects 10, value 2 (non-empty 2), structure 0, missing 0 , absent 8
                                                            pages 1, reply 1203 chars, 71 ms

  HostSystem.summary.maxEVCModeKey                   SEEN   objects 10, value 10 (non-empty 10), structure 0, missing 0 , absent 0
                                                            pages 1, reply 2021 chars, 73 ms

  ClusterComputeResource.summary.currentEVCModeKey   FAULT  Other: InvalidPropertyFault

  ClusterComputeResource.summary                     SEEN   objects 3, value 0 (non-empty 0), structure 3, missing 0 , absent 0
                                                            pages 1, reply 9877 chars, 50 ms
      admissionControlInfo <ClusterFailoverResourcesAdmissionControlInfo> x3
        currentCpuFailoverResourcesPercent x3
        currentMemoryFailoverResourcesPercent x3
        currentPMemFailoverResourcesPercent x3
      clusterMaintenanceModeStatus x3
      currentBalance x3
      currentEVCGraphicsModeKey x3
      currentEVCModeKey x1
      currentFailoverLevel x3
      dasData <ClusterDasDataSummary> x3
        clusterConfigVersion x3
        compatListVersion x3
        hostListVersion x3
      drsScore x3
      effectiveCpu x3
      effectiveMemory x3
      numCpuCores x3
      numCpuThreads x3
      numEffectiveHosts x3
      numHosts x3
      numVmotions x3
      numVmsPerDrsScoreBucket x15
      overallStatus x3
      targetBalance x3
      totalCpu x3
      totalMemory x3
      usageSummary x3
        cpuDemandMhz x3
        cpuEntitledMhz x3
        cpuReservationMhz x3
        memDemandMB x3
        memEntitledMB x3
        memReservationMB x3
        poweredOffCpuReservationMhz x3
        poweredOffMemReservationMB x3
        poweredOffVmCount x3
        statsGenNumber x3
        totalCpuCapacityMhz x3
        totalMemCapacityMB x3
        totalVmCount x3
      vcsHealthStatus x3
      vcsSlots x1
        datastore <Datastore> x31
        host <HostSystem> x1
        systemId x1
        totalSlots x1

  HostSystem.config.certificate                      SEEN   objects 10, value 10 (non-empty 10), structure 0, missing 0 , absent 0
                                                            pages 1, reply 548454 chars, 493 ms

  HostSystem.configManager.certificateManager        SEEN   objects 10, value 10 (non-empty 10), structure 0, missing 0 , absent 0
                                                            pages 1, reply 2639 chars, 49 ms

=== M8.4 detail ===
  VirtualCdrom devices          130 on 130 VMs
    backing VirtualCdromRemoteAtapiBackingInfo            100, connected 0
    backing VirtualCdromRemotePassthroughBackingInfo       15, connected 0
    backing VirtualCdromIsoBackingInfo                     15, connected 2
  VMs with a connected CD       2
  connectable children          startConnected(130), allowGuestControl(130), connected(130), status(130)
  mounted hosts per datastore   1 host(s): 12, 10 host(s): 29

=== M8.7 detail ===
  config.certificate            parses as X.509 on 10 of 10 hosts, notAfter readable on 10

  HostCertificateManager.certificateInfo             SEEN   objects 10, value 0 (non-empty 0), structure 0, missing 1 NoPermission x10, absent 0
                                                            pages 1, reply 5199 chars, 27 ms
  HostCertificateManager with certificateInfo.notAfter: 0 of 10

  LicenseManager.licenses                            SEEN   objects 1, value 0 (non-empty 0), structure 0, missing 1 NoPermission x1, absent 0
                                                            pages 1, reply 849 chars, 22 ms
  licenses 0; property keys: 
  vCenter TLS handshake          certificate seen, notAfter readable: True

=== Host profile compliance (QueryComplianceStatus, no filter) ===
  FAULT InvalidName: A specified parameter was not correct: profile   28 ms

=== Host profile compliance (QueryComplianceStatus, entity = 10 hosts) ===
  results 0   reply 408 chars   27 ms

=== M8.9 appliance REST with the same read-only account (status codes only) ===
  POST /api/session                          201
  session created                            yes
  GET /api/appliance/health/system           403
  GET /api/appliance/recovery/backup/job     403
  DELETE /api/session                        204
```
