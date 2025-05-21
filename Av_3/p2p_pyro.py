import Pyro5.api
import threading
import time
import random
import sys
import os
import base64

TRACKER_HEARTBEAT_INTERVAL = 10
NODE_TIMEOUT_RANGE = (15, 30)

class Peer:
    def __init__(self, nome):
        self.nome = nome
        self.uri = None
        self.pasta = f"./{self.nome}"
        self.isTracker = False
        self.active = True
        self.trackerUri = None
        self.trackerNome = None
        self.lastHeartbeat = time.time()
        self.epoca = 0
        self.peers = []
        self.jaVotou = False
        self.arquivosPeers = {}
        self.eleicaoRodando = False
        self.timeout = random.uniform(*NODE_TIMEOUT_RANGE)

        if not os.path.exists(self.pasta):
            print(f"Erro: o diretorio {self.pasta} nao existe.")
            sys.exit(1)
        
        self.arquivos = os.listdir(self.pasta)
    
    @Pyro5.api.expose
    def cadastrarArquivos(self, nomePeer, uriPeer, arquivos):
        if self.isTracker:
            self.peers[nomePeer] = uriPeer
            self.arquivosPeers[nomePeer] = arquivos
            print(self.arquivosPeers)

    @Pyro5.api.expose
    def procurarArquivo(self, nomeArquivo):
        if self.isTracker:
            return [peer for peer, files in self.arquivosPeers.items() if nomeArquivo in files]

    def solicitarArquivo(self, nomeArquivo):
        if self.trackerUri:
            try:
                with Pyro5.api.Proxy(self.trackerUri) as trackerProxy:
                    possuemArquivo = trackerProxy.procurarArquivo(nomeArquivo)
                    if possuemArquivo:
                        print(f"{self.nome} encontrou o arquivo com: {possuemArquivo}")

                        with Pyro5.api.locate_ns() as ns:
                            for peerNome in possuemArquivo:
                                if peerNome not in self.peers:
                                    try:
                                        self.peers[peerNome] = ns.lookup(peerNome)
                                        print(f"Adicionado {peerNome} aos peers com URI {self.peers[peerNome]}")
                                    except Pyro5.errors.NamingError:
                                        print(f"Erro: Não foi possível localizar {peerNome} no servidor de nomes")
                        
                        peerEscolhido = random.choice(possuemArquivo)
                        uriPeerEscolhido = self.peers[peerEscolhido]
                        with Pyro5.api.Proxy(uriPeerEscolhido) as peerProxy:
                            dados = peerProxy.enviarArquivo(nomeArquivo)

                            if isinstance(dados, dict) and "data" in dados and "encoding" in dados:
                                if dados["encoding"] == "base64":
                                    dados_decodificados = base64.b64decode(dados["data"])
                                    with open(os.path.join(self.pasta, nomeArquivo), "wb") as f:
                                        f.write(dados_decodificados)
                                    print(f"{self.nome} recebeu o arquivo '{nomeArquivo}' com sucesso de {peerEscolhido}")
                                    self.atualizarArquivos(nomeArquivo)
                                else:
                                    print(f"Codificação desconhecida: {dados['encoding']}")
                            else:
                                print(f"Dados inválidos recebidos de {peerEscolhido}")
                                
                    else:
                        print(f"Nenhum peer possui o arquivo desejado.")

            except Pyro5.errors.CommunicationError:
                print(f"{self.nome} nao conseguiu se comunicar com o tracker.")
    
    @Pyro5.api.expose
    def enviarArquivo(self, nomeArquivo):
        arquivo = os.path.join(self.pasta, nomeArquivo)
        if os.path.exists(arquivo):
            print(f"{self.nome} esta enviando o arquivo '{nomeArquivo}'")
            try:
                with open(arquivo, "rb") as f:
                    return f.read()
            except FileNotFoundError:
                print(f"{self.nome} nao encontrou o arquivo '{nomeArquivo}' para enviar.")
        else:
            print(f"{self.nome} nao possui o arquivo '{nomeArquivo}'")
            return None

    def heartbeat(self):
        while self.isTracker and self.active:
            print(f"peers para enviar heartbeat: {self.peers}")
            for nome, peer_uri in self.peers.items():
                if nome != self.nome:
                    try:
                        with Pyro5.api.Proxy(peer_uri) as peer:
                            print(f"enviando heartbeat para {nome}")
                            peer.receberHeartbeat(self.trackerNome, self.epoca)
                    except Pyro5.errors.CommunicationError:
                        print(f"{self.nome} nao conseguiu enviar heartbeat para {peer_uri}")
            
            time.sleep(TRACKER_HEARTBEAT_INTERVAL)
    
    @Pyro5.api.expose
    def receberHeartbeat(self, trackerNome, trackerEpoca):
        if trackerEpoca == self.epoca:
            self.lastHeartbeat = time.time()
            print(f"{self.nome} recebeu heartbeat do tracker {trackerNome}")
        elif trackerEpoca > self.epoca:
            self.epoca = trackerEpoca
            tracker = Pyro5.api.Proxy(f"PYRONAME:{trackerNome}")
            tracker.cadastrarArquivos(self.nome, self.uri, self.arquivos)


    def verificarTracker(self):
        while self.active and not self.isTracker:
            if self.trackerUri:
                elapsed = time.time() - self.lastHeartbeat
                print(elapsed)
                print(self.timeout)
                if elapsed > self.timeout:
                    print(f"{self.nome} detectou falha do tracker.")
                    print(self.peers)
                    print(self.trackerNome)

                    # removendo tracker da lista de peers ativos
                    del self.peers[self.trackerNome]

                    # removendo tracker do servidor de nomes
                    ns = Pyro5.api.locate_ns()
                    ns.remove(self.trackerNome)
                    self.trackerNome = None
                    self.trackerUri = None
                    self.iniciarEleicao()
                    break
            else:
                self.iniciarEleicao()
                break

            time.sleep(self.timeout)

    @Pyro5.api.expose
    def pedirVoto(self, candidato, epoca):
        if self.epoca > epoca:
            return False
        else:
            if self.jaVotou:
                return False
            else:
                self.jaVotou = True
                print(f"{self.nome} votou para o {candidato} na epoca {epoca}")
                return True
            
    @Pyro5.api.expose
    def iniciouEleicao(self):
        if self.eleicaoRodando:
            return True
        return False

    def iniciarEleicao(self):
        # verificar se a eleicao ja nao foi iniciada por outro peer
        with Pyro5.api.locate_ns() as ns:
            self.peers = ns.list()
            print(f"peers no ns ao rodar eleicao: {self.peers}")
            
            primeiro_item = list(peer.peers.items())[0]
            del peer.peers[primeiro_item[0]]

        for peerNome, peerUri in self.peers.items():
            if peerNome != self.nome:
                try:
                    with Pyro5.api.Proxy(peerUri) as peerProxy:
                        print(f"Verificando eleição em {peerNome} com URI {peerUri}")
                        if peerProxy.iniciouEleicao():
                            return
                except Pyro5.errors.CommunicationError:
                    print(f"{peerNome} nao encontrado")

        self.eleicaoRodando = True

        if not self.jaVotou:
            self.epoca += 1
            votos = [self.nome]
            self.jaVotou = True
            print(f"{self.nome} iniciou uma eleicao para a epoca {self.epoca}")
            print(f"peers ativos: {self.peers}")
            for peerNome, peerUri in self.peers.items():
                if peerNome != self.nome:
                    try:
                        with Pyro5.api.Proxy(peerUri) as peerProxy:
                            if peerProxy.pedirVoto(self.nome, self.epoca):
                                votos.append(peerNome)
                    except Pyro5.errors.CommunicationError:
                        print(f"{self.nome} nao encontrou {peerUri} para a eleicao")
            
            if len(votos) > len(self.peers) // 2:
                self.isTracker = True
                self.arquivosPeers = {}
                self.arquivosPeers[self.nome] = self.arquivos
                print(f"{self.nome} agora eh o tracker para a epoca {self.epoca}")

                # registra novo tracker para a epoca no servidor de nomes
                self.trackerNome = f"Tracker_Epoca_{self.epoca}"
                with Pyro5.api.locate_ns() as ns:
                    ns.remove(self.nome)
                    ns.register(self.trackerNome, self.uri)
                    print(f"{self.trackerNome} registrado com URI {self.uri}")

                self.peers[self.trackerNome] = self.peers.pop(self.nome)
                self.arquivosPeers[self.trackerNome] = self.arquivosPeers.pop(self.nome)
                self.nome = self.trackerNome

                # avisa os peers que eh o novo tracker
                for peerNome, peerUri in self.peers.items():
                    if peerNome != self.nome:
                        try:
                            with Pyro5.api.Proxy(peerUri) as peerProxy:
                                peerProxy.reconhecerNovoTracker(self.trackerNome, self.uri)
                                print(f"{peerNome} reconheceu o novo Tracker {self.trackerNome}")
                        except Pyro5.errors.CommunicationError:
                            print(f"{peerNome} nao reconheceu o novo tracker {self.trackerNome}")

                threading.Thread(target=self.heartbeat).start()

        self.eleicaoRodando = False

    @Pyro5.api.expose
    def reconhecerNovoTracker(self, trackerNome, trackerUri):
        self.jaVotou = False
        self.isTracker = False
        self.trackerNome = trackerNome
        self.trackerUri = trackerUri
        self.peers[trackerNome] = trackerUri
        self.peers = {nome: uri for nome, uri in self.peers.items() if not (nome.startswith("Tracker_Epoca_") and nome != self.trackerNome)}
        self.lastHeartbeat = time.time()
        
    def atualizarArquivos(self, novoArquivo):
        self.arquivos.append(novoArquivo)
        if self.trackerUri:
            try:
                with Pyro5.api.Proxy(self.trackerUri) as trackerProxy:
                    trackerProxy.cadastrarArquivos(self.nome, self.uri, self.arquivos)
            except Pyro5.errors.CommunicationError:
                print(f"{self.nome} nao conseguiu atualizar o tracker com novos arquivos.")

if __name__ == "__main__":
    time.sleep(2)
    if len(sys.argv) > 1:
        peer_nome = sys.argv[1]
    else:
        print("Insira o nome do peer.")
        sys.exit(1)
    
    peer = Peer(peer_nome)

    def interface():
        while True:
            print("\nMenu:")
            print("1 - Mostrar arquivos locais")
            print("2 - Solicitar arquivo")
            print("3 - Sair")
            opcao = input("Escolha uma opcao: ")

            if opcao == "1":
                print("Arquivos locais disponiveis: ")
                for arq in peer.arquivos:
                    print(f"- {arq}")
            elif opcao == "2":
                file_name = input("Digite o nome do arquivo: ")
                peer.solicitarArquivo(file_name)
            elif opcao == "3":
                print("Saindo...")
                peer.active = False
                break
            else:
                print("Escolha invalida.")

    with Pyro5.server.Daemon() as daemon:
        uri = daemon.register(peer)
        peer.uri = uri
        with Pyro5.api.locate_ns() as ns:
            ns.register(peer_nome, uri)
            print(f"{peer_nome} registrado com uri {uri}")

            peer.peers = ns.list()
            print(f"peers no ns: {peer.peers}")

            primeiro_item = list(peer.peers.items())[0]
            del peer.peers[primeiro_item[0]] 
            print(peer.peers)
        
        for nome, uri in peer.peers.items():
            print(nome)
            if "Tracker" in nome:
                print(f"Encontrado: Nome = {nome}, URI = {uri}")
                peer.trackerUri = uri
                peer.trackerNome = nome
                tracker = Pyro5.api.Proxy(f"PYRONAME:{peer.trackerNome}")
                tracker.cadastrarArquivos(peer.nome, peer.uri, peer.arquivos)
                peer.lastHeartbeat = time.time()
                break

        tracker_thread = threading.Thread(target=peer.verificarTracker)
        tracker_thread.start()

        interface_thread = threading.Thread(target=interface)
        interface_thread.start()

        try:
            daemon.requestLoop(lambda: peer.active)
        finally:
            interface_thread.join()
            tracker_thread.join()