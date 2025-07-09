import grpc
from concurrent import futures
import replicacao_dados_pb2 as pb2
import replicacao_dados_pb2_grpc as pb2_grpc
from database import init_db, insert_log, fetch_log_entry

class ReplicaService(pb2_grpc.ReplicaServiceServicer):
    def __init__(self, replica_id):
        self.replica_id = replica_id
        self.db_intermediario = init_db(f"{replica_id}_intermediario.db")
        self.db_final = init_db(f"{replica_id}_final.db")

    def ReceiveDataFromLeader(self, request, context):
        local_entry = self.get_entry_by_offset(request.offset)

        # Se a entrada já existe e for igual, não precisa fazer nada
        if local_entry == (request.epoch, request.offset, request.content):
            print(f"[{self.replica_id}] Entrada já presente. Ignorando.")
            return pb2.Ack(message="ACK")

        # Se já tem uma entrada diferente nesse offset → truncar e sincronizar
        if local_entry is not None and local_entry != (request.epoch, request.offset, request.content):
            print(f"[{self.replica_id}] Divergência no offset {request.offset}. Iniciando sincronização...")
            self.truncate_log_from_offset(request.offset)
            self.sync_log_from_leader(request.epoch, request.offset)
            return pb2.Ack(message="ACK")

        # Se a entrada está à frente (gap), buscar os dados faltantes
        if local_entry is None:
            max_offset = self.get_max_offset()
            if request.offset > max_offset + 1:
                print(f"[{self.replica_id}] Gap detectado. Sincronizando de {max_offset+1} até {request.offset}")
                self.sync_log_from_leader(request.epoch, max_offset + 1)

            # Insere a nova entrada normalmente
            insert_log(self.db_intermediario, request.epoch, request.offset, request.content)
            print(f"[{self.replica_id}] Entrada sincronizada/recebida: {request}")
            return pb2.Ack(message="ACK")

    def CommitData(self, request, context):
        result = fetch_log_entry(self.db_intermediario, request.offset)
        if result:
            epoch, offset, content = result
            insert_log(self.db_final, epoch, offset, content)
            print(f"[{self.replica_id}] COMMIT: {epoch}, {offset}, {content}")
        return pb2.Ack(message="Commit ok")

    def get_entry_by_offset(self, offset):
        cursor = self.db_intermediario.cursor()
        cursor.execute("SELECT epoch, offset, content FROM log WHERE offset = ?", (offset,))
        return cursor.fetchone()

    def get_max_offset(self):
        cursor = self.db_intermediario.cursor()
        cursor.execute("SELECT MAX(offset) FROM log")
        result = cursor.fetchone()
        return result[0] if result[0] is not None else -1

    def truncate_log_from_offset(self, offset):
        cursor = self.db_intermediario.cursor()
        cursor.execute("DELETE FROM log WHERE offset >= ?", (offset,))
        self.db_intermediario.commit()
        print(f"[{self.replica_id}] Log truncado a partir do offset {offset}")

    def sync_log_from_leader(self, epoch, start_offset):
        channel = grpc.insecure_channel("localhost:50051")
        stub = pb2_grpc.LeaderServiceStub(channel)
        request = pb2.SyncLogRequest(epoch=epoch, offset=start_offset)
        response = stub.SyncLog(request)
        for entry in response.entries:
            insert_log(self.db_intermediario, entry.epoch, entry.offset, entry.content)
            print(f"[{self.replica_id}] Sincronizado do líder: {entry}")


def serve(replica_id, port):
    server = grpc.server(futures.ThreadPoolExecutor(max_workers=10))
    pb2_grpc.add_ReplicaServiceServicer_to_server(ReplicaService(replica_id), server)
    server.add_insecure_port(f"[::]:{port}")
    print(f"{replica_id} rodando na porta {port}...")
    server.start()
    server.wait_for_termination()

if __name__ == "__main__":
    import sys
    serve(sys.argv[1], sys.argv[2])