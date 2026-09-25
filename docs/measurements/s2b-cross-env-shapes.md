# S2b — SimpliVity çapraz-ortam kontrolleri, canlı şekil ölçümü (25 Eylül 2026)

`probe --from-store KibarHolding-KBVc01 --candidates-s2b`, salt-okunur servis
hesabı, canlı vCenter (59 host, 1100 VM, 32 küme). Her aday yol **tek
başına** okundu; `QueryLockdownExceptions` her host için bir kez çağrıldı.
Çıktı ad, tip ve sayı taşır; tek istisna vmkernel port group adları (hangi
vmk'nin SimpliVity depolaması olduğu HPE'nin adlandırmasından okunuyor,
varsayılmıyor). Test fikstürleri (`tests/…Vsphere.Tests/Fixtures/S2b/`) bir
OVC'nin, host'unun ve kümesinin canlı yanıtları; adres, MAC, anahtar UUID'si,
moRef numaraları ve bir port group adı maskelendi.

## Karar tablosu

| Yol / çağrı | Görüldü | Şekil | Hata | Maliyet (hızlı okuma, `--time-inventory`) | Listeye |
|---|---|---|---|---|---|
| `VirtualMachine.config.memoryAllocation.reservation` | 1100/1100 | değer (`xsd:long`, MB) | — | 181 B/VM | **evet** (limit'in yanında) |
| `VirtualMachine.resourceConfig.memoryAllocation.reservation` | 1100/1100 | değer | — | 172 B/VM tek başına | hayır: yukarıdakiyle 1100/1100 aynı |
| `VirtualMachine.resourcePool` | 1050/1100 | değer (moRef `ResourcePool`); şablonda yok (50) | — | 196 B/VM | **evet** |
| `ClusterComputeResource.resourcePool` | 32/32 | değer (kök havuz moRef) | — | 205 B/küme | **evet** |
| `HostSystem.config.network.vnic` | 59/59 | yapı: `HostVirtualNic` ×102 (`device`, `portgroup`, `spec.mtu`, `spec.distributedVirtualPort` ×30) | — | 1 699 B/host | **evet** |
| `HostSystem.config.network.vswitch` (zaten okunuyor) | 59/59 | `HostVirtualSwitch` ×106, `mtu` ×106, `portgroup` = `key-vim.host.PortGroup-<ad>` | — | — | zaten listede |
| `HostSystem.configManager.hostAccessManager` | 59/59 | değer (moRef `HostAccessManager`) | — | 238 B/host | **evet** |
| `HostAccessManager.QueryLockdownExceptions` | 59/59 | boş yanıt (`returnval` yok) | **izinli** (NoPermission yok) | 412 karakter/çağrı | **yalnız lockdown açık host'ta**; Kibar'da 0 çağrı |
| `HostSystem.summary.hardware.cpuModel` | 59/59 | değer | — | 193 B/host tek başına | hayır (aşağıda) |
| `configurationEx.rule` / `.group` (zaten okunuyor) | 5 `ClusterVmHostRuleInfo`, 7 `ClusterHostGroup`, 5 `ClusterVmGroup` | — | — | — | zaten listede |

Hızlı okumanın yeni maliyeti: VM başına 377 B (1100 VM → ~415 KB), host
başına 1 937 B (~114 KB), küme başına 205 B. Hızlı okuma toplamı 4,48 MB,
4,7 s; yenilerin payı ~%12. Tam envanter (`--shapes`) yeni listeyle hatasız,
hiçbir `MISSING` satırı yok.

## Maskeli alıntılar

```
HostVirtualNic: device vmk1, portgroup SVT_StorPG, spec.mtu 9000
HostVirtualNic: device vmk0, portgroup (boş), spec.distributedVirtualPort …, spec.mtu 1500
HostVirtualSwitch: mtu 9000, portgroup key-vim.host.PortGroup-{VMkernel, SVT_FedPortGroup, SVT_StoragePortGroup, SVT_StorPG}
QueryLockdownExceptionsResponse: (boş)
```

vmkernel port group'ları (59 host): `SVT_StorPG` mtu 9000 ×26, dağıtık port
mtu 1500 ×30, `Management Network` 1500 ×23, `MgmtNET` 1500 ×19, diğer 4.
Standart anahtar MTU'su (collector'ın ayrıştırıcısıyla): `SVT_StorPG=9000`,
`SVT_StoragePortGroup=9000`, `SVT_FedPortGroup=9000`, her biri ×26.
Federasyonun vmkernel bağdaştırıcısı yok; OVC'nin federasyon ve depolama
NIC'leri `SVT_FedPortGroup` / `SVT_StoragePortGroup` üzerinde.

## Kibar'da beklenen hükümler (26 SimpliVity host'u)

OVC, host'un `simplivity.virtual_controller_name`'i ile aynı adlı ve o
host'ta çalışan VM (26/26 eşleşiyor; örn. `OmniStackVC-…` → RunsOn o host).

| Kontrol | Passing | Failing | NotEvaluated | Dayanak |
|---|---|---|---|---|
| `svt.ovc-reservation` | 26 | 0 | 0 | rezervasyon = memoryMB 26/26 |
| `svt.ovc-not-in-pool` | 26 | 0 | 0 | kök havuzda 26/26 |
| `svt.lockdown-exception` | 26 | 0 | 0 | `lockdownDisabled` 59/59 |
| `svt.vmk-mtu` | 26 | 0 | 0 | yukarıdaki ×26 satırlar |
| `svt.drs-must-group` | 26 | 0 | 0 | estate'te host başına en çok 5 VM zorunlu grupta |

NotEvaluated olabilecek durumlar: OVC adı okunmadı ya da o host'ta o adda VM
yok; rezervasyon/havuz/kök havuz okunmadı; lockdown açık ve liste okunamadı
ya da boş olmayan bir liste (Digital Vault hesabının adı toplanmıyor);
depolama vmk'si dağıtık portta (port group adı yok).

## Eklenmeyenler

- `svt.ha-on-mva-cluster`: `eo-cont.ha-enabled` her kümede zaten aynı hükmü
  veriyor; MVA'nın hangi kümede olduğu toplanmıyor (ürün ilkesi 4).
- `svt.evc-mixed-cpu`: `eo-cont.maint-evc` aynı kuralı `maxEVCModeKey` ile
  veriyor. `cpuModel` ile yapılsaydı 2 küme (5 kümeden) aynı EVC nesline
  rağmen "karışık" sayılırdı (2 model, 1 mod).
