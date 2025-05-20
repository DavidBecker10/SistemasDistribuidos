import Pyro5.api
import threading
import time
import random
import sys
import os

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
                        peerEscolhido = random.choice(possuemArquivo)
                        with Pyro5.api.Proxy(peerEscolhido) as peerProxy:
                            dados = peerProxy.enviarArquivo(nomeArquivo)
                            if dados:
                                with open(os.path.join(self.pasta, f"recebido_{nomeArquivo}"), "wb") as f:
                                    f.write(dados)
                                print(f"{self.nome} recebeu o arquivo '{nomeArquivo}' com sucesso de {peerEscolhido}")
                                self.atualizarArquivos(nomeArquivo)
                                return True
                            else: 
                                print(f"{self.nome} nao recebeu o arquivo '{nomeArquivo}' de {peerEscolhido}")
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
            for nome, peer_uri in self.peers.items():                         # enviar a epoca aqui, assim o peer sabe se eh da epoca que ele esta esperando ou nao
                try:                                            # e caso nao seja ele pode fazer todo o processo de se cadastrar no tracker 
                    with Pyro5.api.Proxy(peer_uri) as peer:
                        print(f"enviando heartbeat para {nome}")
                        peer.receberHeartbeat(self.nome, self.epoca)
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
            tracker.cadastrarArquivos(peer.nome, peer.uri, peer.arquivos)


    def verificarTracker(self):
        while self.active and not self.isTracker:
            if self.trackerUri:
                timeout = random.uniform(*NODE_TIMEOUT_RANGE)
                elapsed = time.time() - self.lastHeartbeat
                print(elapsed)
                print(timeout)
                if elapsed > timeout:
                    print(f"{self.nome} detectou falha do tracker.")
                    print(self.peers)
                    print(self.trackerNome)
                    del self.peers[self.trackerNome]
                    self.iniciarEleicao()
                    break
            else:
                self.iniciarEleicao()
                break

            time.sleep(timeout)

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

    def iniciarEleicao(self):
        self.epoca += 1
        votos = [self.nome]
        self.jaVotou = True
        print(f"{self.nome} iniciou uma eleicao para a epoca {self.epoca}")
        print(f"peers ativos: {self.peers}")
        for nome, peerUri in self.peers.items():
            try:
                with Pyro5.api.Proxy(peerUri) as peerProxy:
                    if peerProxy.pedirVoto(self.nome, self.epoca):
                        votos.append(peerUri)
            except Pyro5.errors.CommunicationError:
                print(f"{self.nome} nao encontrou {peerUri} para a eleicao")
        
        if len(votos) > len(self.peers) // 2:
            self.isTracker = True
            self.arquivosPeers = {}
            print(f"{self.nome} agora eh o tracker para a epoca {self.epoca}")

            # registra novo tracker para a epoca no servidor de nomes
            self.trackerNome = f"Tracker_Epoca_{self.epoca}"
            with Pyro5.api.locate_ns() as ns:
                ns.remove(self.nome)
                ns.register(self.trackerNome, uri)
                print(f"{self.trackerNome} registrado com URI {uri}")

            threading.Thread(target=self.heartbeat).start()

            # registra arquivos no novo tracker
            for peerUri in self.peers.values():
                try:
                    with Pyro5.api.Proxy(peerUri) as peerProxy:
                        peerProxy.cadastrarArquivos(self.nome, self.arquivos)
                except Pyro5.errors.CommunicationError:
                    print(f"{self.nome} nao encontrou {peerUri}")
        
    def atualizarArquivos(self, novoArquivo):
        self.arquivos.append(novoArquivo)
        if self.trackerUri:
            try:
                with Pyro5.api.Proxy(self.trackerUri) as trackerProxy:
                    trackerProxy.cadastrarArquivos(self.nome, self.arquivos)
            except Pyro5.errors.CommunicationError:
                print(f"{self.noem} nao conseguiu atualizar o tracker com novos arquivos.")

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
            peer.peers = ns.list()
            print(f"peers no ns: {peer.peers}")
            primeiro_item = list(peer.peers.items())[0]
            del peer.peers[primeiro_item[0]]
            print(peer.peers)
            ns.register(peer_nome, uri)
            print(f"{peer_nome} registrado com uri {uri}")
        
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