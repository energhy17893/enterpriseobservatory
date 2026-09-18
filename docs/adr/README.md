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
| [0004](0004-relationship-taxonomy-and-entity-lifecycle.md) | İlişki taksonomisi ve varlık yaşam döngüsü | Kabul edildi |
| [0005](0005-collector-contract-and-collection-discipline.md) | Collector sözleşmesi ve toplama disiplini | Kabul edildi |
| [0006](0006-web-stack-react-spa.md) | Web arayüzü React + TypeScript SPA | Kabul edildi |

## Şablon

Yeni ADR için `_template.md` dosyasını kopyala, numarayı bir artır.
