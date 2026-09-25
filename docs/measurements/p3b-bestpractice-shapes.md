# P3b best-practice — canlı şekil ölçümü (25 Eylül 2026)

`probe --from-store <bağlantı> --candidates-p3b --mask` (KBVc01), salt-okunur
servis hesabı (59 host, 1100 VM). Her aday **tek başına** okundu. Çıktı yalnızca
ad, sayı ve kimlik taşımayan enum değerlerini (güç politikası kısa adı, vmx
sürümü) toplu olarak basar; nesne adı, moRef, IP yok.

Kaynak: [vSphere 8.0U3 Performance Best Practices](https://www.vmware.com/docs/vsphere-esxi-vcenter-server-80U3-performance-best-practices),
[KB 438023](https://knowledge.broadcom.com/external/article/438023/rightsizing-virtual-machines-on-esxi-80.html)
(reference-approaches §9.2).

## Karar tablosu

| Yol | Görüldü | Şekil | Hata | Maliyet (tek başına) | Listeye |
|---|---|---|---|---|---|
| `HostSystem.config.powerSystemInfo.currentPolicy` | 59/59 | tek yapı, alan adsız 4 parçaya düzleşiyor | — | 18,1 KB | hayır — alan adı kayboluyor |
| `HostSystem.config.powerSystemInfo.currentPolicy.shortName` | 59/59 | değer | — | 10,8 KB | **evet**, yapılandırma katmanı |
| `HostSystem.hardware.numaInfo` | 59/59 | yapı: `numNodes`, `type`, `numaNode` ×160 (`cpuID` ×5864, `pciId` ×15828, …) | NoPermission **yok** | **559 KB** (~9,5 KB/host) | hayır — `pciId` listesi kimsenin okumadığı yük |
| `HostSystem.hardware.numaInfo.numNodes` | 59/59 | değer | — | 9,1 KB | **evet**, yapılandırma katmanı |
| `HostSystem.hardware.cpuInfo.numCpuCores` | 59/59 | değer | — | 9,4 KB | **evet**, yapılandırma katmanı |
| `VirtualMachine.config.cpuHotAddEnabled` | 1100/1100 | değer | — | 175 KB | **evet**, hızlı katman |
| `VirtualMachine.config.version` | 1100/1100 | değer | — | 165 KB | **evet**, hızlı katman |

NUMA düğüm genişliği = `numCpuCores / numNodes` (fiziksel çekirdek). Kılavuzun
koşulu çekirdek sayar; `numaNode[].cpuID` mantıksal CPU (HT) sayar, düğüm
başına 16–64 — çekirdeğin iki katı. Bu yüzden `numaInfo` bütün yerine iki skaler.

`numaInfo` salt-okunur role açık: `eo-bp.cpu-hot-add-vnuma` için zayıf yedek
("hot-add açık ve vmx-20 altı") gerekmedi.

## Yavaş okuma farkı (`--time-inventory --timeout-minutes 9`)

Birleşik istekte propSet baytı:

| Tip | Yol | Bayt / nesne | Toplam |
|---|---|---|---|
| HostSystem (yavaş) | `config.powerSystemInfo.currentPolicy.shortName` | 200 | 11,8 KB |
| HostSystem (yavaş) | `hardware.cpuInfo.numCpuCores` | 176 | 10,4 KB |
| HostSystem (yavaş) | `hardware.numaInfo.numNodes` | 171 | 10,1 KB |
| VirtualMachine (hızlı) | `config.cpuHotAddEnabled` | 175 | 193 KB |
| VirtualMachine (hızlı) | `config.version` | ~165 ("other" satırında) | ~180 KB |

Yavaş katman: +547 B/host (+32 KB, 33,2 MB'ın %0,1'i). `numaInfo` bütün
alınsaydı ~9,5 KB/host olurdu. Hızlı katman: ~+0,37 MB (4,35 MB'ın ~%8'i).

## Bu estate'te beklenen hükümler

```
  hosts by power policy shortName   dynamic(33), static(26)
  hosts not on 'static' (High Performance)   33
  hosts by NUMA nodes               1(2), 2(41), 4(13), 8(3)
  hosts by cores per NUMA node      8(6), 9(1), 10(1), 12(17), 16(3), 18(1), 24(21), 28(2), 32(7)
  NUMA nodes by logical CPU count   16(14), 18(8), 20(2), 24(43), 32(17), 36(2), 48(56), 56(4), 64(14)
  VMs by hardware version           vmx-07(2), vmx-08(31), vmx-09(3), vmx-10(44), vmx-11(150), vmx-13(143), vmx-14(4), vmx-15(8), vmx-17(354), vmx-18(1), vmx-19(226), vmx-20(8), vmx-21(126)
  VMs by cpuHotAddEnabled           false(466), true(634)
  VMs hot-add on and below vmx-20   630
    of those, numCPU > host cores per NUMA node   32
    of those, host or its NUMA size unknown       0
```

- `eo-bp.host-power-policy`: **33 Failing** (Balanced), 26 Passing.
- `eo-bp.cpu-hot-add-vnuma`: **32 Failing**. Zayıf koşul 630 VM'de tutardı —
  §9.2'nin reddettiği "VM başına donanım sürümü bulgusu" gürültüsü; kılavuzun
  üç koşulu bunu 32'ye indiriyor.
