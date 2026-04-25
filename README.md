# Controle de Contas Steam

Aplicativo desktop em WPF para gerenciar contas Steam, drops e profit por conta.

## Funcionalidades

- Dashboard de contas com vitorias, bans e rank
- Tela de drops pendentes
- Cadastro e edicao de contas com PIN
- Controle de drops por conta
- Profit por conta com total e media
- Aplicacao em instancia unica

## Tecnologias

- .NET 9
- C#
- WPF
- XAML
- JSON

## Como rodar

```powershell
dotnet restore
dotnet build
dotnet run
```

## Publicacao

```powershell
dotnet publish .\ControleDeContasSteam.csproj -c Release -r win-x64 --self-contained false
```

## Dados locais

Os dados editados no programa ficam salvos localmente em:

```text
%LocalAppData%\ControleDeContasSteam\dados.json
```

Esse arquivo nao faz parte do repositorio e nao deve ser enviado para o GitHub.

## PIN inicial

No primeiro uso, o PIN padrao e:

```text
1234
```

Recomenda-se alterar apos abrir o aplicativo.

## Estrutura principal

- `MainWindow.xaml`: layout principal
- `MainWindow.xaml.cs`: logica da interface
- `App.xaml.cs`: inicializacao e controle de instancia unica
- `Assets/`: imagens e icones usados na interface
