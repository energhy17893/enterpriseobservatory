# Katkı ve Çalışma Kuralları

Bu belge, kod tabanının **devredilebilir** kalmasını sağlayan kuralları
tanımlar. Gerekçeleri için [ADR-0002](docs/adr/0002-adr-and-commit-discipline.md).

## Kurulum

```bash
git clone https://github.com/energhy17893/enterpriseobservatory.git
cd enterpriseobservatory
git config core.hooksPath .githooks   # commit mesajı doğrulaması için ZORUNLU
dotnet build
dotnet test
```

`core.hooksPath` ayarını yapmazsan commit'lerin formatı doğrulanmaz ve CI'da
reddedilirler.

## Commit formatı

[Conventional Commits](https://www.conventionalcommits.org/). `commit-msg`
hook'u tarafından doğrulanır.

```
<tip>(<kapsam>): <özet>

<gövde>

<altbilgi>
```

### Tipler

| Tip | Ne zaman |
|---|---|
| `feat` | Yeni yetenek |
| `fix` | Hata düzeltmesi |
| `refactor` | Davranış değişmeden yapı değişikliği |
| `perf` | Performans iyileştirmesi |
| `test` | Yalnızca test ekleme/düzeltme |
| `docs` | Yalnızca dokümantasyon (ADR dahil) |
| `build` | Derleme sistemi, bağımlılık |
| `ci` | CI/CD yapılandırması |
| `chore` | Diğer bakım işleri |
| `revert` | Önceki commit'i geri alma |

### Kapsamlar

`domain`, `application`, `collectors`, `api`, `host`, `persistence`, `hosting`,
`web`, `adr`, `deps`, veya belirli bir modül adı.

### Özet satırı

- Emir kipi, İngilizce: `add`, `fix`, `remove` — `added`, `fixes` değil
- Küçük harfle başla, sonunda nokta yok
- En fazla 72 karakter

### Gövde — en önemli kısım

Gövde **ne** yapıldığını anlatmaz; diff zaten söylüyor. Gövde şunları anlatır:

1. **Neden** bu değişikliğe ihtiyaç vardı — hangi problem, hangi gözlem
2. **Neden bu şekilde** — hangi alternatif değerlendirildi ve neden seçilmedi
3. Varsa **hangi ödünü** verdik

Önemsiz bir değişiklik (yazım hatası, bağımlılık sürümü) için gövde
gerekmez. Bir davranışı veya yapıyı değiştiren her commit için gerekir.

### Örnek

```
feat(domain): add sticky clear to alert lifecycle

Operatör bir alarmı temizlediğinde, koşul altyapıda hâlâ sürüyor olsa bile
yeniden bildirim gönderilmemeli. Aksi halde kalıcı bir donanım arızası
(örn. değişimi bekleyen PSU) operatörü her poll döngüsünde spam'ler ve
bildirimlere olan güveni yok eder.

ClearedByOperator bayrağı fingerprint kaybolup yeni bir instance doğana
kadar korunur. Alternatif olarak süre tabanlı bir sessizleştirme (örn. 24
saat) düşünüldü; seçilmedi çünkü arızanın ne zaman gerçekten çözüleceğini
bilmiyoruz ve keyfi bir süre sonunda alarm yeniden patlar.
```

## Dal stratejisi

Trunk tabanlı. `main` her zaman yeşil ve yayınlanabilir olmalı.

- Küçük, düşük riskli değişiklikler doğrudan `main`'e gidebilir.
- Bir özellik birden fazla commit alıyorsa veya riskliyse:
  `feat/<kısa-ad>` dalı aç, PR ile birleştir.
- Dallar kısa ömürlüdür — birkaç günden uzun yaşayan dal, çok büyük demektir.

## Pull request

PR açıklaması `.github/pull_request_template.md` şablonunu doldurur.
"Neden" alanı zorunludur.

Birleştirme yöntemi: **squash merge.** Squash commit mesajı yukarıdaki
kurallara uymalıdır — dal içindeki ara commit'ler serbesttir.

## Mimari kurallar

Bunlar konvansiyon değil, `EnterpriseObservatory.Architecture.Tests`
tarafından zorlanan ve CI'da kırmızıya düşen kurallardır:

1. `Domain` hiçbir projeye referans veremez.
2. `Application` yalnızca `Domain`'e referans verir.
3. Collector'lar birbirini göremez.
4. `Domain` içinde `static` mutable state olamaz.
5. `Domain` ve `Application` içinde I/O tipi (`HttpClient`, `File`,
   `NpgsqlConnection`) kullanılamaz.

Bir kuralı esnetmen gerekiyorsa, testi değiştirmeden önce bir ADR yaz.

**Yeni bir proje eklerken:** EnterpriseObservatory.Architecture.Tests'e ondan
bir proje referansı ekle. Kurallar test çıktı dizinindeki assembly'leri tarar;
referans yoksa yeni proje sessizce denetlenmez.

## Mimari karar gerektiğinde

`docs/adr/_template.md` dosyasını kopyala, numarayı bir artır, doldur.
Ne zaman ADR gerektiği [docs/adr/README.md](docs/adr/README.md) içinde.

## Sürümleme

SemVer, git etiketlerinden türetilir. Elle sürüm numarası yazılmaz.
CHANGELOG commit'lerden üretilir.
