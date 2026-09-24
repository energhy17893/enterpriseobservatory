# Fold dilimi — canlı ölçüm ve uyarlanır boyut

*24 Eylül 2026. Kaynak: host log'u (EventId 1060 "Fold slice", 1015
"Compaction failed") ve `compaction` / `series` / `sample` tabloları.
Bu dosya ölçüm kaydıdır; kararın gerekçesi `PostgresObservationStore`
içindeki `DefaultBucketsPerSlice` notunda.*

## Sorun

Beş dakikalık katmanın `FoldFromSamples` komutu Npgsql `CommandTimeout`
(30 sn, `PostgresDatabase.cs`) sınırına çarpıyor.

- Bugün **74** "Compaction failed" kaydı; **14:40Z–14:56Z** arasında başarılı
  dilim yok.
- `compaction` satırı: FiveMinutes `completed_to` 14:55Z, `dirty_from`
  13:55Z — kirli bir saatin yeniden katlanması.

## Yeniden başlatmadan sonra (6 kovalık dilim)

| katman | dilim | yazılan kova | süre |
|---|---|---|---|
| FiveMinutes | 6 kova | 100.174 | 27,6 sn |
| FiveMinutes | 6 kova | 111.126 | 28,3 sn |

İkisi de 30 sn sınırının 2 sn altında. 23 Eylül'deki 12 → 6 yarılaması
(17,5 / 25,7 / 18,1 sn ölçümleri) bugünkü yükte yetmedi.

## Estate

| ölçü | değer |
|---|---|
| `series` satırı | 33.809 |
| `sample` tablosu | 5,9 GB |
| saat başına 5 dk kova, 23 Eylül | 137k–183k |
| saat başına 5 dk kova, 24 Eylül | 203k |

## Sonuç

Dilim maliyeti ≈ seri × kova. Seri sayısı müşterinin estate'ine ait, sabit
bir dilim boyutu satılan üründe yanlış. Uygulanan davranış (katman başına,
bellekte, yeniden başlatmada sıfırlanır):

- Zaman aşımında dilim yarıya iner (taban 1), aynı dilim yeniden denenir —
  işlem geri alındığı için watermark kıpırdamamıştır.
- Art arda 3 dilim 10 sn altındaysa boyut iki katına çıkar, üst sınır
  `BucketsPerSlice` (varsayılan 6).
- 1 kovalık dilim de zaman aşımına uğrarsa eski yol: "Compaction failed;
  the next pass will pick it up."
- `CommandTimeout` 30 sn kalır.
- 1060 satırı kullanılan dilim boyutunu taşır; boyut değişimi 1063 ile bir
  kez INF yazılır.

Ölçülmeyen: uyarlanır boyutla canlıda dilim süreleri (yayımdan sonra 1060 /
1063 satırlarından okunacak).
