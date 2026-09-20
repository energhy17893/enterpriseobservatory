# Mimari Karar Kayıtları (ADR)

Bu dizin, projenin mimarisini şekillendiren her önemli kararı kalıcı olarak
kaydeder. Amaç basit: **altı ay sonra "bu neden böyle yapılmış?" sorusunun
cevabı kodun arkeolojisinde değil, burada olsun.**

## Neden ADR tutuyoruz?

Bu ürünün önceki sürümü versiyon kontrolü olmadan, ~130 MSI sürümü boyunca
geliştirildi. Kararların gerekçeleri sohbet geçmişlerinde ve geliştiricinin
aklında kaldı. Sonuç: kod tabanı teknik olarak sağlamdı ama **devredilemezdi.**

ADR'ler bunu yapısal olarak çözer. Bir karar kaydedilmemişse, o karar alınmamış
sayılır.

## Ne zaman ADR yazılır?

Bir karar aşağıdakilerden **en az birini** sağlıyorsa ADR gerektirir:

- Geri alınması pahalı (veri modeli, dağıtım topolojisi, kalıcılık seçimi)
- Birden fazla makul alternatif vardı ve birini seçtik
- Sezgiye aykırı (birinin gelip "bu neden böyle?" diye soracağı bir şey)
- Bir ilkeden ödün veriyor veya bir ilkeyi somutlaştırıyor

Bir karar sadece "tek makul yol buydu" ise ADR gerekmez.

## Durum akışı

```
Önerildi ──→ Kabul edildi ──→ Yerini aldı (başka bir ADR ile)
     │                    └──→ Terk edildi
     └──→ Reddedildi
```

Kabul edilmiş bir ADR **asla silinmez ve düzenlenmez.** Karar değiştiyse yeni bir
ADR yazılır ve eskisinin durumu "Yerini aldı: ADR-00XX" olarak güncellenir.
Geçmişi silmek, ADR tutmanın amacını ortadan kaldırır.

## Dizin

| # | Başlık | Durum |
|---|---|---|
| [0001](0001-modular-monolith-with-deployment-agnostic-core.md) | Deployment-agnostik çekirdekli modüler monolit | Kabul edildi |
| [0002](0002-adr-and-commit-discipline.md) | ADR ve commit disiplini, dil seçimi | Kabul edildi |
| [0003](0003-topology-first-entity-model.md) | Topoloji-önce varlık modeli | Kabul edildi |
| [0004](0004-relationship-taxonomy-and-entity-lifecycle.md) | İlişki taksonomisi ve varlık yaşam döngüsü | Kabul edildi — §2 sağlık yayılımı sütunu → ADR-0018 |
| [0005](0005-collector-contract-and-collection-discipline.md) | Collector sözleşmesi ve toplama disiplini | Kabul edildi |
| [0006](0006-web-stack-react-spa.md) | Web arayüzü React + TypeScript SPA | Kabul edildi |
| [0007](0007-information-architecture-and-alert-centralization.md) | Bilgi mimarisi ve alarm merkezileştirme | Kabul edildi |
| [0008](0008-design-token-architecture.md) | Tasarım token mimarisi ve kontrast kapısı | Kabul edildi |
| [0009](0009-evaluation-scope-and-provenance.md) | Değerlendirme kapsamı ve köken damgası | Kabul edildi |
| [0010](0010-credentials-never-in-configuration-files.md) | Kimlik bilgileri yapılandırma dosyasında bulunmaz | Kabul edildi |
| [0011](0011-sqlite-state-persistence.md) | Durum kalıcılığı için gömülü SQLite | **Geçersiz kılındı → ADR-0016** |
| [0012](0012-time-series-storage-and-downsampling.md) | Zaman serisi depolama ve aşağı örnekleme | Kabul edildi — saklama süreleri → ADR-0017 |
| [0013](0013-operator-actions-and-attribution.md) | Operatör eylemleri ve atıf | Kabul edildi (§2 yerini ADR-0014'e bıraktı) |
| [0014](0014-local-accounts-and-cookie-sessions.md) | Yerel hesaplar ve çerez oturumları | Kabul edildi |
| [0015](0015-connections-entered-in-the-product.md) | Bağlantılar üründen girilir, parola şifrelenmiş saklanır | Kabul edildi |
| [0016](0016-external-infrastructure-dependencies.md) | Dış altyapı bağımlılıkları — PostgreSQL tek depolama motoru | Kabul edildi — §5 saklama gerekçesi → ADR-0023 |
| [0017](0017-retention-windows-set-by-measurement.md) | Saklama pencereleri ölçümle yeniden belirlendi | Kabul edildi — alternatif C → ADR-0023 |
| [0018](0018-health-derives-from-alerts-not-propagation.md) | Sağlık alarmlardan türer, kenarlardan yayılmaz | Kabul edildi |
| [0019](0019-empty-series-rows-are-kept.md) | Boş seri satırları silinmez, birikmesine izin verilir | Kabul edildi |
| [0020](0020-key-ring-durability-is-checked-at-startup.md) | Anahtar halkasının kaybolabilirliği açılışta denetlenir, servis durdurulmaz | Kabul edildi |
| [0021](0021-blind-spot-stays-per-datastore.md) | Depolama gecikmesi kör noktası datastore başına raporlanır | Kabul edildi |
| [0022](0022-postgres-tests-run-in-a-linux-job.md) | PostgreSQL testleri ayrı bir Linux işinde, tek kullanımlık parolayla çalışır | Kabul edildi |
| [0023](0023-postgres-dependency-rejustified.md) | PostgreSQL bağımlılığının gerekçesi yeniden kuruldu | Kabul edildi |

## Şablon

Yeni ADR için `_template.md` dosyasını kopyala, numarayı bir artır.
