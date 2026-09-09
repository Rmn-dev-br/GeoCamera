# GeoCamera

Aplicativo MAUI simples para Android: câmera traseira, iniciar/parar vídeo e acessar gravações. Latitude, longitude, precisão e horário são desenhados em branco sobre uma caixa preta totalmente opaca **nos pixels do MP4 durante a gravação**. A etiqueta na tela é apenas uma prévia do texto.

## Instalar no telefone

1. Abra `GeoCamera.slnx` (ou o projeto `GeoCamera.csproj`) no Visual Studio com a carga de trabalho .NET MAUI/Android.
2. No telefone, ative as opções do desenvolvedor e a depuração USB. Conecte por USB e autorize o computador.
3. Selecione o telefone como destino Android e execute o projeto.

Alternativamente, compile e instale pelo PowerShell:

```powershell
dotnet build GeoCamera.csproj -f net10.0-android -c Debug
& 'C:\Program Files (x86)\Android\android-sdk\platform-tools\adb.exe' install -r 'bin\Debug\net10.0-android\com.companyname.geocamera-Signed.apk'
```

O APK assinado também pode ser copiado para o telefone e aberto para instalar; autorize a instalação dessa origem quando o Android solicitar. O projeto incorpora os assemblies ao APK, dispensando os arquivos de implantação rápida do Visual Studio. O APK gerado inclui ARM64 (telefones de 64 bits) e x86_64 (emuladores).

## Usar

- Autorize câmera e localização durante o uso. Ative a localização do telefone. A permissão de microfone é opcional: se negada, o vídeo fica sem áudio.
- Aguarde a imagem e toque em **Iniciar gravação**. Toque em **Parar e salvar** após alguns segundos.
- A localização é consultada repetidamente. Uma posição com mais de 15 segundos é marcada como desatualizada; sem permissão/sinal, o vídeo recebe uma mensagem explícita em vez de coordenadas inventadas.
- **Gravações** permite selecionar um vídeo para **Abrir** ou **Compartilhar / salvar cópia** usando os aplicativos instalados.
- Ao sair do aplicativo ou bloquear a tela, a gravação é encerrada e salva quando possível. A tela permanece ligada durante a gravação normal.

## Arquivos e formato

Os MP4 ficam no armazenamento privado persistente do aplicativo: `FileSystem.AppDataDirectory/Recordings` (normalmente `/data/user/0/com.companyname.geocamera/files/Recordings`). Não aparecem automaticamente na galeria. Use compartilhar/salvar cópia para exportar. Desinstalar o aplicativo remove esses arquivos privados.

Vídeo vertical H.264, 480 × 640, alvo de 15 quadros/s e 2 Mbit/s; áudio AAC quando autorizado. A implementação captura a textura da câmera, compõe a legenda em um bitmap e envia os quadros via OpenGL/EGL para o MediaRecorder. A taxa efetiva depende do telefone; a escolha modesta de resolução limita o custo da composição. O aplicativo usa a API nativa de câmera compatível com Android 5.0+ e não acrescenta dependências NuGet. Os outros destinos originais do projeto foram preservados, mas a câmera/gravação é exclusiva do Android.

## Validação no aparelho

O build não substitui esta verificação com câmera/GPS reais:

1. Grave por pelo menos 10 segundos, mude de posição e abra/compartilhe o MP4. Confira orientação, áudio e texto branco sobre preto no vídeo aberto fora do app.
2. Negue microfone e repita: o vídeo deve continuar sem áudio. Negue localização ou desligue o GPS e confira a mensagem no arquivo.
3. Saia do app durante a gravação e retorne: o vídeo deve estar na lista e a câmera deve reabrir.
4. Teste uma parada imediata e pouca capacidade livre: deve aparecer erro sem apresentar um arquivo incompleto como gravação válida.

Validação nesta implementação: `dotnet build GeoCamera.csproj -f net10.0-android -c Debug` concluiu com restauração atualizada, zero erros e zero avisos. O APK foi inspecionado para confirmar assemblies incorporados e a configuração de compartilhamento da pasta `Recordings`. O ADB não encontrou dispositivos conectados. Não foi realizado teste em telefone físico ou emulador; câmera, GPS, sincronismo de áudio e desempenho ainda precisam da verificação acima no aparelho.
