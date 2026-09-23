# A8 tekrar sayısı ve E2 olay arama/sayfalama — sorgu maliyeti (5433)

*24 Eylül 2026. Test sunucusu 5433 (PostgreSQL 18), taze şema, `ANALYZE` sonrası.
Ölçüm testi:* `tests/EnterpriseObservatory.Persistence.Postgres.Tests/InboxQueryMeasurementTests.cs`
*(`dotnet test ... --filter InboxQueryMeasurementTests --logger "console;verbosity=detailed"`).
Bu dosya ölçüm kaydıdır; eşik yayımlamaz, test yalnız sonuçların doğruluğunu tutar.*

## Veri

- `alert_history`: **10.000 satır**, 2.000 parmak izi, parmak izi başına 1–4 yaşam
  (her yaşam 2 geçiş). Parmak izi `AlertFingerprint.Create` ile üretildi.
- `source_event`: **11.000 satır** (Kibar'da 10.825), 30 güne yayılı, tip kimlikleri
  `EventAlerts.Catalogue`'tan (59 tip), mesaj ~110 karakter, 59 host, 1.100 VM.

"Store call" = ürünün kendi store metodu, ısınmış, 5 çalıştırmanın medyanı
(bağlantı havuzu, gidiş-dönüş dahil). "Exec" = `EXPLAIN (ANALYZE, BUFFERS)`
Execution Time.

## A8 — `EpisodeCounts` (sayfa başına tek sorgu)

| durum | store call medyan / maks | exec | plan |
|---|---|---|---|
| 50 parmak izi (düz sayfa) | 8,2 / 8,7 ms | 6,7 ms | Seq Scan, 250 buffer |
| 1.000 parmak izi (gruplu sayfa) | 26,2 / 28,6 ms | — | — |

Planlayıcı 10k satırda benzersiz anahtarın indeksi yerine sıralı taramayı seçti
(tablo 250 sayfa). İndeks var — `UNIQUE (fingerprint, episode_first_seen_utc, ordinal)`
— tablo büyüdükçe planlayıcı ona geçer. **Migration gerekmedi.**

## E2 — `IEventStore.Recent(offset, limit, source, search)`

| durum | store call medyan / maks | exec (EXPLAIN) |
|---|---|---|
| ilk sayfa, arama yok | 3,0 / 5,4 ms | — |
| son sayfa (offset 10.950), arama yok | 8,7 / 11,5 ms | 23,4 ms (Incremental Sort, `ix_source_event_created` geri tarama) |
| arama, sık geçen (`vm-01`) | 44,0 / 46,6 ms | sayfa sorgusu 0,65 ms; maliyet sayımda |
| arama, eşleşme yok (en kötü) | **103,1 / 162,0 ms** | sayım 104,4 ms (Seq Scan, 11.000 satır, 5 × ILIKE) |
| arama, derin sayfa (offset 500, `esx.`) | 130,4 / 151,5 ms | — |

- **Offset yeterli**, imleç gerekmedi: en derin sayfa 8,7 ms.
- Aramanın maliyeti toplam sayımda (`count(*)`): eşleşme olmayınca tablonun tamamı
  beş kolonda ILIKE ile taranıyor, ~9,5 µs/satır.
- Planlayıcının eşiği 200 ms: en kötü durum medyanı 103 ms, maks 162 ms → **indeks yok**.
  Tablo 10 kat büyürse (≈110k satır) doğrusal olarak ~1 s beklenir; o zaman trigram/GIN
  indeksi migration 18 olur (planlayıcının kararı, ayrı PR).
