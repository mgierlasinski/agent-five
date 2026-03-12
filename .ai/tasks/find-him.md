## Zadanie

Musisz namierzyć, która z podejrzanych osób zapisanych w pliku people_transport.json **przebywała blisko jednej z elektrowni atomowych.** Musisz także ustalić jej **poziom dostępu** oraz informację koło której elektrowni widziano tę osobę. Zebrane tak dane prześlij do `/verify`. Nazwa zadania to **findhim**.

#### Skąd wziąć dane?

1. **Lista elektrowni + ich kody**
   - Pobierz JSON z listą elektrowni (wraz z kodami identyfikacyjnymi) z:
     - `https://hub.ag3nts.org/data/<HubApiKey>/findhim_locations.json`
     
   Przykładowa odpowiedź:
   ```json
    {
        "power_plants": {
            "Zabrze": {
                "is_active": true,
                "power": "35 MW",
                "code": "PWR3847PL"
            },
            "Żarnowiec": {
                "is_active": false,
                "power": "0 MW",
                "code": "PWR6132PL"
            }
        }
    }
    ```

2. **Gdzie widziano konkretną osobę (lokalizacje)**

   - Endpoint: `https://hub.ag3nts.org/api/location`
   - Metoda: `POST`
   - Body: `raw JSON` (nie form-data!)
   - Zawsze wysyłasz pole `apikey` oraz dane osoby (`name`, `surname`)
   - Odpowiedź: lista współrzędnych (koordynatów), w których daną osobę widziano.

   Przykładowy payload:

   ```json
   {
     "apikey": "<HubApiKey>",
     "name": "Jan",
     "surname": "Kowalski"
   }
   ```

   Przykładowa odpowiedź:

   ``` json
   [
    {
        "latitude": 50.448,
        "longitude": 8.761
    },
    {
        "latitude": 52.652,
        "longitude": 16.825
    }
   ]
   ```

3. **Jaki poziom dostępu ma wskazana osoba**

   - Endpoint: `https://hub.ag3nts.org/api/accesslevel`
   - Metoda: `POST`
   - Body: `raw JSON`
   - Wymagane: `apikey`, `name`, `surname` oraz `birthYear` (dane z pliku people_transport.json)

   Przykładowy payload:

   ```json
   {
     "apikey": "<HubApiKey>",
     "name": "Jan",
     "surname": "Kowalski",
     "birthYear": 1987
   }
   ```

   Przykładowa odpowiedź:

   ```json
   {
    "name": "Jan",
    "surname": "Kowalski",
    "accessLevel": 7
   }
   ```

#### Co masz zrobić krok po kroku?

Dla każdej osoby:

1. Pobierz listę jej lokalizacji z `/api/location`.
2. Porównaj otrzymane koordynaty z koordynatami elektrowni z `findhim_locations.json`.
3. Jeśli lokalizacja jest bardzo blisko jednej z elektrowni — masz kandydata.
4. Dla tej osoby pobierz `accessLevel` z `/api/accesslevel`.
5. Zidentyfikuj **kod elektrowni** (format: `PWR0000PL`) i przygotuj raport.

#### Wysłanie odpowiedzi

Na koniec wysyłasz odpowiedź metodą **POST** na `https://hub.ag3nts.org/verify`.

Nazwa zadania to: **findhim**.

Pole `answer` to **pojedynczy obiekt** zawierający:

- `name` – imię podejrzanego
- `surname` – nazwisko podejrzanego
- `accessLevel` – poziom dostępu z `/api/accesslevel`
- `powerPlant` – kod elektrowni z `findhim_locations.json` (np. `PWR1234PL`)

Przykład JSON do wysłania na `/verify`:

```json
{
  "apikey": "<HubApiKey>",
  "task": "findhim",
  "answer": {
    "name": "Jan",
    "surname": "Kowalski",
    "accessLevel": 3,
    "powerPlant": "PWR1234PL"
  }
}
```

### Ważne uwagi

- **Zrealizuj zadanie w podejściu agentowym**
- **Dane wejściowe** — lista podejrzanych pochodzi z pliku people_transport.json generowanego przez PeopleTask. Potrzebujesz imienia, nazwiska i roku urodzenia każdej osoby.
- **Obliczanie odległości geograficznej** — API zwraca współrzędne (latitude/longitude). Żeby sprawdzić, czy dana lokalizacja jest "bardzo blisko" elektrowni, użyj wzoru na odległość na kuli ziemskiej np. Haversine. Szukamy osoby która była najbliżej którejś elektrowni.
- **Wykorzystaj Function Calling** — model LLM zamiast tylko odpowiadać tekstem wywołuje także zdefiniowane przez program funkcje (narzędzia). Program opisuje narzędzia w formacie JSON Schema (nazwa, opis, parametry), a model sam decyduje, które wywołać i z jakimi argumentami. Program obsługuje wywołania i zwraca wyniki z powrotem do modelu. W tym zadaniu Function Calling sprawdza się szczególnie dobrze: agent może samodzielnie iterować przez listę podejrzanych, odpytywać kolejne endpointy i wysłać gotową odpowiedź — bez sztywnego kodowania kolejności kroków w kodzie.
- **Komunikacja z LLM przez OpenRouter** - wykorzystaj klasę OpenRouterService, dokonaj niezbędnych zmian np. do obsługi tooli.
- **Format `birthYear`** — endpoint `/api/accesslevel` oczekuje roku urodzenia jako liczby całkowitej (np. `1987`). Jeśli Twoje dane zawierają pełną datę (np. `"1987-08-07"`), pamiętaj o wyciągnięciu samego roku przed wysłaniem żądania.
- **Zabezpieczenie pętli agenta** — ustal maksymalną liczbę iteracji (np. 10-15), żeby uchronić się przed nieskończoną pętlą w razie błędu modelu.
- **Jak znaleźć lokalizację elektrowni?** - ponieważ dane z zadania nie precyzują lokalizacji elektrowni jako współrzędne, spróbuj przekształcić współrzędne, pod którymi byli użytkownicy w nazwy miejsc - użyj modelu LLM
- do implementacji wykorzystaj klasę FindHimTask, całość utrzymaj w ramach ficzera FindHim.
- loguj do konsoli requesty i odpowiedzi
- przed wysłaniem odpowiedzi do /verify, zapisz payload do pliku findhim_verify.json
