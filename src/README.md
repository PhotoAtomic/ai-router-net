# AI Router

Un router .NET 10 per il routing delle richieste AI verso diversi provider (Anthropic, llama.cpp locale, ecc.) basato sul nome del modello.

## Caratteristiche

- Routing basato su pattern regex (valutati in ordine)
- Supporto per API Anthropic compatibili (Anthropic, llama.cpp, ecc.)
- Passthrough completo di tutte le features
- Configurazione tramite file `appsettings.json` o variabili d'ambiente
- Supporto per più chiavi API

## Configurazione

### appsettings.json

Il file di configurazione utilizza le regole di routing `RoutingRules`, valutate in ordine dalla prima all'ultima. La prima regola il cui pattern regex matcha il nome del modello viene attivata.

```json
{
  "RoutingRules": [
    {
      "Pattern": "^claude-3(-[a-z]+)?(-[a-z]+)?(-\\d{4})?$",
      "BaseUrl": "https://api.anthropic.com/v1"
    },
    {
      "Pattern": "^llama-3\\.1-[0-9]+[b]?$",
      "BaseUrl": "http://localhost:8080/v1"
    },
    {
      "Pattern": "^llama-3\\.2-[0-9]+[b]?$",
      "BaseUrl": "http://localhost:8080/v1"
    },
    {
      "Pattern": "^mixtral$",
      "BaseUrl": "http://localhost:8080/v1"
    },
    {
      "Pattern": "^gpt-[0-9]+(-[a-z]+)?$",
      "BaseUrl": "https://api.openai.com/v1"
    }
  ],
  "ApiKeys": {
    "anthropic": "${ANTHROPIC_API_KEY}",
    "local": "${LOCAL_API_KEY}",
    "openai": "${OPENAI_API_KEY}"
  },
  "DefaultApiKey": "${DEFAULT_API_KEY}",
  "Host": "http://0.0.0.0",
  "Port": "5000"
}
```

### Formato delle regole di routing

Ogni regola di routing è un oggetto con due campi:

| Campo | Tipo | Descrizione |
|-------|------|-------------|
| `Pattern` | string | Pattern regex da applicare al nome del modello |
| `BaseUrl` | string | URL di base del provider di destinazione |

### Valutazione delle regole

Le regole vengono valutate **in ordine**:
1. Il router prende il nome del modello dalla richiesta
2. Per ogni regola (dalla prima all'ultima):
   - Se il pattern regex matcha il nome del modello → usa quel `BaseUrl`
   - Altrimenti → passa alla regola successiva
3. Se nessuna regola matcha → restituisce errore 404

### Esempi di pattern regex

```json
{
  "Pattern": "^claude-3(-[a-z]+)?(-[a-z]+)?(-\\d{4})?$"
}
```
Matcha: `claude-3-5-sonnet`, `claude-3-opus`, `claude-3-haiku-2024`

```json
{
  "Pattern": "^llama-3\\.1-[0-9]+[b]?$"
}
```
Matcha: `llama-3.1-70b`, `llama-3.1-8b`, `llama-3.1-405b`

```json
{
  "Pattern": "^gpt-[0-9]+(-[a-z]+)?$"
}
```
Matcha: `gpt-4`, `gpt-4-turbo`, `gpt-3.5-turbo`

### Variabili d'ambiente

Le seguenti variabili d'ambiente sono supportate:

- `ANTHROPIC_API_KEY` - Chiave API per Anthropic
- `LOCAL_API_KEY` - Chiave API per server locali (opzionale)
- `OPENAI_API_KEY` - Chiave API per OpenAI (opzionale)
- `DEFAULT_API_KEY` - Chiave API di default
- `Host` - Host di ascolto (default: `http://0.0.0.0`)
- `Port` - Porta di ascolto (default: `5000`)

Le chiavi API possono essere referenziate nel config file usando il formato `${VARIABLE_NAME}`.

## Utilizzo

### Avvio

```bash
dotnet run
```

### Pubblicazione Windows

```powershell
dotnet publish -c Release -r win-x64 --self-contained true
```

L'eseguibile viene generato in `bin\Release\net10.0\win-x64\publish`. Il servizio usa `appsettings.json` dalla stessa cartella dell'eseguibile, quindi la configurazione resta valida anche quando Windows avvia il processo da `C:\Windows\System32`. Anche il salvataggio delle regole dalla dashboard aggiorna questo stesso file.

### Installazione come servizio Windows

Da una console PowerShell avviata come amministratore, usando direttamente l'eseguibile pubblicato:

```powershell
cd "C:\Program Files\AiRouter"
.\AiRouter.exe --install-service --start
```

Lo stesso `AiRouter.exe` funziona quindi in entrambe le modalita': se lanciato senza comandi parte come normale applicazione console, se lanciato con `--install-service` registra se stesso come servizio Windows.

In alternativa, dallo stesso repository:

```powershell
.\scripts\Install-WindowsService.ps1 -Start
```

Comandi utili:

```powershell
Start-Service AiRouter
Stop-Service AiRouter
Get-Service AiRouter
.\AiRouter.exe --uninstall-service
```

Per abilitare logging e dashboard quando AiRouter parte come servizio, registra il servizio passando `--log` negli argomenti runtime:

```powershell
.\AiRouter.exe --install-service --service-args "--log" --start
```

Con `--log` senza percorso, `requests.jsonl` viene creato nella stessa cartella di `AiRouter.exe`. Anche un percorso relativo, ad esempio `--log requests.jsonl`, viene risolto rispetto alla cartella dell'eseguibile. Se il servizio era gia' installato senza logging, rimuoverlo e reinstallarlo:

```powershell
.\AiRouter.exe --uninstall-service
.\AiRouter.exe --install-service --service-args "--log" --start
```

Per usare un percorso log esplicito:

```powershell
.\AiRouter.exe --install-service --service-args "--log ""C:\ProgramData\AiRouter\requests.jsonl""" --start
```

Per verificare gli argomenti registrati nel servizio:

```powershell
sc.exe qc AiRouter
```

### Esempio di richiesta

```bash
curl -X POST http://localhost:5000/v1/chat/completions \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer your-api-key" \
  -d '{
    "model": "claude-3-5-sonnet",
    "messages": [{"role": "user", "content": "Hello!"}],
    "max_tokens": 100
  }'
```

## Endpoint

- `POST /v1/chat/completions` - Endpoint principale per le chat completions

## Output di debug

Il router stampa in console:
- Le regole di routing caricate (in ordine)
- Il nome del modello dalla richiesta
- La regola che ha matchato
- L'URL di destinazione

Esempio:
```
AI Router starting on http://0.0.0.0:5000
Routing rules (evaluated in order):
  ^claude-3(-[a-z]+)?(-[a-z]+)?(-\d{4})?$ -> https://api.anthropic.com/v1
  ^llama-3\.1-[0-9]+[b]?$ -> http://localhost:8080/v1
  ...

Routing request for model: claude-3-5-sonnet
  Matched rule: ^claude-3(-[a-z]+)?(-[a-z]+)?(-\d{4})?$ -> https://api.anthropic.com/v1
Forwarding to: https://api.anthropic.com/v1/chat/completions
```

## Note

- Il router effettua un semplice passthrough delle richieste senza conversione di protocollo
- Tutte le features delle API Anthropic sono supportate
- Le regole vengono valutate in ordine: la prima che matcha vince
- I pattern regex usano la sintassi .NET `System.Text.RegularExpressions`
- Le chiavi API possono essere specificate nel config file o tramite variabili d'ambiente
