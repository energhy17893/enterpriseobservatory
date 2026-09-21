# Okuma bütçesi — 2000 VM'lik sentetik ölçüm (T1.1, paket A)

*22 Eylül 2026. Harness: `tests/EnterpriseObservatory.Collectors.Vsphere.Tests/ReadBudgetMeasurementTests.cs`
(+ `SimulatedVcenter.cs`). Çalıştırma:*

```
dotnet test tests/EnterpriseObservatory.Collectors.Vsphere.Tests --filter "FullyQualifiedName~ReadBudgetMeasurement" --logger "console;verbosity=detailed"
```

## Özet — bugünkü kod (önce)

| | Değer |
|---|---|
| VM sayaç sayısı | 17 |
| Başlangıç batch'i (`maxQueryMetrics` = 256) | `floor(256 × 0,8 / 17)` = **12 VM / sorgu** |
| 2000 VM'lik tam gözlem okuması | **170 çağrı** (167 `QueryPerf` + katalog + limit + probe) |
| Süre, çağrı başına 250 / 500 / 1000 ms | **42,5 / 85 / 170 sn** |
| Gözlem zaman aşımı (`SourceTimeout`, sabit) | 25 sn — aralık 30 sn |
| **Sığıyor mu?** | **Hayır, üç gecikmede de.** Okuma 25 sn'de bırakılıyor (`AbandonedReadException`); okuma ya hep ya hiç olduğu için **2000 VM'in hiçbirinin örneği saklanmıyor**, her döngüde. |
| Başabaş | 25 sn'ye 167 sorgu için çağrı başına ≤ ~150 ms gerekir; 250 ms'de ~1150 VM, 500 ms'de ~560 VM'e kadar sığar. |

**Ana bulgu:** mevcut sıralı okuma, 2000 VM'i 30 sn'lik aralığa gerçekçi hiçbir
gecikmede sığdıramıyor. Kısmi ilerleme (T1.1) bunu "hiç veri yok"tan "bütçeye
sığan kadar veri + kalanı açıkça okunmadı" durumuna çevirir; ama tamamını
sığdırmak **tek oturumda sıralı sorgu ile mümkün değil** — paralel sorgu (ADR-0005
§5 aile başına tavan, T2.3) ya da gevşek limit yorumunda daha büyük batch
gerekir. Bu, planlayıcı için ayrı bir karar.

## Varsayımlar

- **Çağrı gecikmesi ölçülmedi.** Bu estate'te hiçbir `QueryPerf` gidiş-dönüşü
  zamanlanmadı; üç değer taranıyor (250 / 500 / 1000 ms, sabit, batch boyutundan
  bağımsız). Canlı ölçüm açık iş.
- Sunucu limiti **katı** yorumla uygulanıyor (varlık × sayaç ≤ limit) — ADR-0005 §2.
- Sanal saat: çağrılar gerçek zaman harcamıyor; süre = çağrı sayısı × gecikme.
  Soru zaten çağrı dizisi üzerinde aritmetik.
- Yalnızca VM'ler (host/datastore yok). Gerçek 2000 VM'lik bir estate ~80 host
  daha ekler: +~5 sorgu (host sayaç sayısı daha büyük, batch daha küçük).
- **Envanter bu harness'ta simüle edilmedi.** Sayfa boyutu 250 ile 2000 VM ≥ 8
  `RetrieveProperties` sayfası + host/datastore/ağ sayfaları eder; 25 sn'ye
  sığması sayfa başına ≲ 2 sn gerektirir. Ölçülmedi — sayfa gecikmesi canlıda
  bilinmiyor. Envanter zaman aşımı T1.1 ile aralıktan türetildi (bkz. "Sonra").

## Senaryolar — önce

Her senaryo aynı kaynak örneğiyle iki döngü okundu (döngü 2 = öğrenilen bir şey
hatırlanıyor mu).

| senaryo | gecikme | döngü | çağrı | QueryPerf | ret | sn | örnekli VM | hatalar |
|---|---|---|---|---|---|---|---|---|
| limit okunabilir (256) | 250 ms | 1 | 170 | 167 | 0 | 42,5 | 2000* | — |
| limit okunabilir (256) | 250 ms | 2 | 170 | 167 | 0 | 42,5 | 2000* | — |
| limit okunamıyor, gerçek 64 | 250 ms | 1 | 672 | 669 | 2 | 168,0 | 2000* | batch küçültüldü |
| limit okunamıyor, gerçek 64 | 250 ms | 2 | 672 | 669 | **2** | 168,0 | 2000* | batch küçültüldü |
| vm-1 silinmiş | 250 ms | 1 | 3 | 0 | 0 | 0,8 | **0** | VirtualMachine (tüm tip) |
| vm-1000 silinmiş | 250 ms | 1 | 87 | 84 | 0 | 21,8 | **996** | VirtualMachine (tüm tip) |
| limit okunabilir (256) | 500 ms | 1 | 170 | 167 | 0 | 85,0 | 2000* | — |
| limit okunamıyor, gerçek 64 | 500 ms | 1 | 672 | 669 | 2 | 336,0 | 2000* | batch küçültüldü |
| vm-1000 silinmiş | 500 ms | 1 | 87 | 84 | 0 | 43,5 | 996* | VirtualMachine (tüm tip) |
| limit okunabilir (256) | 1000 ms | 1 | 170 | 167 | 0 | 170,0 | 2000* | — |
| limit okunamıyor, gerçek 64 | 1000 ms | 1 | 672 | 669 | 2 | 672,0 | 2000* | batch küçültüldü |

\* Kaynağın *döndürdüğü*; runner 25 sn'de okumayı bıraktığı için gerçekte
**saklanan 0**. Tam tablo harness çıktısında (500/1000 ms satırlarının döngü 2'si
döngü 1 ile aynı).

Okunanlar:

- **T1.1** — 42,5 sn'lik okuma 25 sn'lik bütçede atılıyor: 2000 VM için ürün
  hiçbir döngüde tek örnek saklamıyor. Zaman aşımı sabit (25 sn), aralıkla
  bağı yok.
- **T1.2** — gizli limitte (64) her döngü 12 → 6 → 3 diye **yeniden öğreniyor**:
  döngü başına 2 boşa sorgu. Maliyet küçük (0,5–2 sn), ama her döngü bir
  `ProtocolError` "küçültüldü" kaydı bırakıyor ve 3'lük batch 669 sorgu ediyor —
  asıl maliyet limitin kendisi.
- **T1.3** — ret bugün mesaj metniyle tanınıyor ("restricted by administrator").
- **T1.4** — `moRefs[0]` silinmişse probe `ManagedObjectNotFound` ile düşüyor ve
  **2000 VM'in tamamı** okunmuyor. Listenin ortasında silinmiş tek VM de o
  batch'ten sonraki **1004 VM'i** düşürüyor.

## Sonra

*(Adım 2 bitince doldurulacak.)*
