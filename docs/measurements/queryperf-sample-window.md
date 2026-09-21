# QueryPerf örnek penceresi — canlı ölçüm (m1, m2)

*22 Eylül 2026, 21:37Z civarı. Salt-okunur probe:*
`dotnet run --project tools/EnterpriseObservatory.VsphereProbe -- --from-store --mask --time-queryperf 20`
*Estate: 10 host, 145 VM, vCenter 8. Real-time aralık (20 sn), VM sayaç seti (17).
Bu dosya ölçüm kaydıdır; tasarım kararı içermez (handover 3'ün girdisi).*

## m1 — pencere + `maxSample` birlikte verilince hangi uç kesiliyor?

Tek VM, pencere `startTime` = şimdi − 10 dk, `endTime` = şimdi
(21:27:36Z .. 21:37:36Z; 20 sn'de ~30 örnek).

| `maxSample` | dönen örnek | ilk | son |
|---|---|---|---|
| 3 | 3 | 21:36:40Z | 21:37:20Z |
| 30 (kontrol) | 30 | 21:27:40Z | 21:37:20Z |

**Sonuç: `maxSample` pencerenin en yeni ucunu tutuyor, eskiyi kesiyor.**
`maxSample` = 3 pencerenin son üç örneğini döndürdü; kontrol pencerenin tamamını
(30 örnek) gösteriyor. Tek ölçüm, tek VM, real-time aralık — historical
aralıkta `maxSample` zaten yok sayılıyor (metrik sözleşmesi §3).

## m2 — tek sorgu varlık başına kaç örnek taşıyabilir?

12 VM × 17 sayaç × `maxSample` 180 (20 sn'de 1 saat), pencere yok; 5 çağrı.

| çağrı | süre | yanıt | örnek / varlık | kapsam | varlık |
|---|---|---|---|---|---|
| 1 | 1949 ms | 2657,3 KiB | 180 | 20:37:40Z .. 21:37:20Z | 12 |
| 2 | 2466 ms | 2657,3 KiB | 180 | 20:37:40Z .. 21:37:20Z | 12 |
| 3 | 3239 ms | 2657,3 KiB | 180 | 20:37:40Z .. 21:37:20Z | 12 |
| 4 | 2459 ms | 2657,3 KiB | 180 | 20:38:00Z .. 21:37:40Z | 12 |
| 5 | 1596 ms | 2657,3 KiB | 180 | 20:38:00Z .. 21:37:40Z | 12 |

- **Fault yok**, beş çağrının beşi de 180 örnekle döndü (real-time'ın tuttuğu
  bir saatin tamamı).
- Yanıt **~2,6 MiB** (≈ 2,72 MB; istemcinin tavanı `MaxResponseBytes` = 64 MiB).
- Süre medyan **2459 ms**, maks 3239 ms — `maxSample` 3'lü aynı sorgunun
  (~135 ms) ~18 katı; örnek sayısı 60 kat.
- Karşılaştırma için: `maxSample` 3'te yanıt başına 204 değer; 180'de varlık
  başına 180 örnek × 17 sayaç (instance `*` ile cihaz serileri dahil).

Ölçülmeyen: 180'in üstü (real-time bir saatten fazlasını tutmuyor, anlamsız),
daha büyük batch'lerle 180 örnek, eşzamanlı sorgular altında davranış.
