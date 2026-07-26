# Co-edição Jurius

Serviço que permite **duas ou mais pessoas editarem o mesmo `.docx` ao mesmo tempo**
no editor de petições do CRM — o texto de uma aparece na tela da outra enquanto é
digitado.

Ele existe porque o `docs.jurius-api.com` (imagem padrão do Word Processor) **não
tem** os endpoints de co-edição: `UpdateAction`, `GetActionsFromServer` e o hub
SignalR respondem 404.

Base: o exemplo oficial da Syncfusion
([EJ2-Document-Editor-Collaborative-Editing](https://github.com/SyncfusionExamples/EJ2-Document-Editor-Collaborative-Editing)),
com quatro mudanças:

1. o documento é lido e gravado no **Nextcloud** (WebDAV), não em disco local;
2. toda chamada de documento exige **token do Supabase** (o mesmo da sessão do CRM);
3. CORS é **allow-list**, não `*`;
4. `ImportFile` devolve a **versão corrente** da sala — o exemplo devolve `0`, o que
   duplica texto para quem entra depois no documento.

## Subir

```bash
cp .env.example .env   # preencha
docker compose up -d --build
```

O serviço fica em `127.0.0.1:54873` (porta escolhida para não colidir com as suas:
9000, 8080, 8082, 8083, 8084, 42811, 38427). O Redis não publica porta nenhuma.

No túnel da Cloudflare, adicione a rota:

| Hostname | Path | Service |
| --- | --- | --- |
| `collab.jurius-api.com` | `*` | `http://localhost:54873` |

O `cloudflared` já encaminha WebSocket — não precisa de configuração extra. A
própria página inicial confirma isso (veja abaixo).

Depois, no `.env` do CRM:

```
VITE_SYNCFUSION_COLLAB_URL=https://collab.jurius-api.com
```

Sem essa variável, o CRM funciona exatamente como antes: abre o documento direto do
Nextcloud, sem co-edição.

## Página inicial (`https://collab.jurius-api.com`)

Painel ao vivo, atualizado a cada 4 segundos:

- **Status do serviço** — no ar / degradado, há quanto tempo subiu
- **Componentes** — Redis (grava e lê uma chave de verdade), Nextcloud (confirma se
  a credencial é aceita), licença Syncfusion, se o token está sendo exigido
- **Tempo real** — abre um WebSocket de verdade contra `/demohub` e mostra a resposta
  do servidor. É o teste que prova que o túnel da Cloudflare está deixando o
  WebSocket passar
- **Motor de documentos** — botão que monta um `.docx`, abre com o mesmo carregador da
  co-edição e converte para SFDT. Pega na hora o erro clássico de faltar biblioteca
  nativa na imagem
- **Atividade** — documentos abertos agora, pessoas editando, edições recebidas,
  gravações no Nextcloud
- **Documentos em edição** e **últimos acontecimentos** (entrou, saiu, abriu, gravou)

O painel é público de propósito, para você conferir logo depois de subir. Por isso
ele **não** mostra nome de cliente, caminho de arquivo nem conteúdo de documento: a
sala aparece como hash e quem edita aparece só por iniciais (`L.M.`). Para desligar
a página e o teste de WebSocket: `COLLAB_DEMO_ENABLED=false`.

## Quando algo não sobe

O serviço **não morre** por falta de configuração: ele sobe, recusa as rotas de
documento e mostra na página inicial o que está faltando. O `docker logs` também
imprime um resumo logo no start:

```
Co-edição Jurius iniciando · Redis: redis:6379 · Nextcloud: BaseUrl FALTANDO, User FALTANDO … 
```

**Variáveis não chegaram ao container?** É o tropeço mais comum:

- Pela linha de comando: o `.env` precisa estar **na mesma pasta** do
  `docker-compose.yml`. Confirme o que o compose entendeu com:
  ```bash
  docker compose config
  ```
  Se aparecer `Supabase__Url: ""`, o `.env` não foi lido.
- **Pelo Portainer (stack a partir do Git): o `.env` do repositório é ignorado.**
  As variáveis têm de ser cadastradas na aba *Environment variables* da stack —
  com os mesmos nomes do `.env.example` (`SUPABASE_URL`, `NEXTCLOUD_BASE_URL`, …).

**Nextcloud recusando a credencial?** A página inicial diz "credencial recusada"
quando volta 401/403. Use uma *senha de app* do Nextcloud (Configurações →
Segurança), não a senha da conta, e confira se a `BaseUrl` termina no usuário:
`https://…/remote.php/dav/files/USUARIO`.

**"Token exigido mas Supabase não configurado"** (em vermelho na página): nesse
estado toda chamada de documento é recusada com 401, de propósito. Preencha
`SUPABASE_URL` e `SUPABASE_ANON_KEY`.

## Como funciona

```
Editor (navegador)                     Este serviço                 Nextcloud
   │  POST ImportFile ─────────────────────►│  baixa o .docx ──────────►│
   │  ◄──────────── SFDT + versão ──────────│                           │
   │                                        │
   │  digita ─► POST UpdateAction ─────────►│ versiona + transforma (OT)
   │                                        │ guarda no Redis
   │  ◄── SignalR "dataReceived" ───────────│ distribui para a sala
   │                                        │
   │  (última pessoa sai) ─────────────────►│ aplica tudo e grava ─────►│
```

- **Sala** = um arquivo do Nextcloud. O nome é o SHA-256 do caminho, calculado no
  front (`syncfusionCollab.service.ts` no CRM) — o serviço nunca vê o caminho legível
  em log ou painel.
- O Redis guarda as operações ainda não gravadas (limite de 100; ao passar disso, as
  mais antigas vão para o arquivo em background).
- Quando o último participante fecha o documento, o que estiver pendente é aplicado
  ao `.docx` e gravado no Nextcloud.

## Variáveis

| Variável | Para que serve |
| --- | --- |
| `SUPABASE_URL` / `SUPABASE_ANON_KEY` | validar o token de quem chama |
| `NEXTCLOUD_BASE_URL` | raiz WebDAV: `https://…/remote.php/dav/files/USUARIO` |
| `NEXTCLOUD_USER` / `NEXTCLOUD_PASSWORD` | credencial (use senha de app) |
| `SYNCFUSION_LICENSE_KEY` | mesma chave do servidor de documentos |
| `COLLAB_ALLOWED_ORIGINS` | origens do CRM permitidas |
| `COLLAB_AUTH_REQUIRE` | `false` desliga a exigência de token — só em ambiente fechado |
| `COLLAB_DEMO_ENABLED` | `false` desliga a página inicial e o `/demohub` |

> A versão do pacote `Syncfusion.EJ2.WordEditor.AspNet.Core` (no `.csproj`) precisa ser
> a **mesma** do npm `@syncfusion/ej2-documenteditor` do CRM — hoje `32.1.19`. Versões
> diferentes trocam o formato das operações e corrompem o documento.

## Pontos de atenção

- **Com co-edição ligada, o navegador não grava mais o `.docx`** — quem grava é este
  serviço. Gravar dos dois lados aplicaria as operações pendentes duas vezes e
  duplicaria texto. O CRM já respeita isso.
- **Memória**: cada gravação carrega o documento inteiro. Reserve ~1 GB para o
  container.
- **Se o Nextcloud estiver fora do ar** na hora da gravação, o erro vai para o log e
  as operações continuam no Redis (com `appendonly`), mas o `.docx` fica desatualizado
  até a próxima gravação.
- **Este código não foi compilado na máquina de desenvolvimento** (sem SDK .NET lá). O
  `docker compose build` é quem valida — rode antes de apontar o CRM para o serviço.
