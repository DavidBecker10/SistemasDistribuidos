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
        self.log = []

    def ReceiveDataFromLeader(self, request, context):
        if len(self.log) == request.offset:
            self.log.append(request)
            insert_log(self.db_intermediario, request.epoch, request.offset, request.content)
            print(f"[{self.replica_id}] Intermediário: {request}")
            return pb2.Ack(message="ACK")
        else:
            print(f"[{self.replica_id}] Offset inconsistente. Ignorado.")
            return pb2.Ack(message="NACK")

    def CommitData(self, request, context):
        result = fetch_log_entry(self.db_intermediario, request.offset)
        if result:
            epoch, offset, content = result
            insert_log(self.db_final, epoch, offset, content)
            print(f"[{self.replica_id}] COMMIT: {epoch}, {offset}, {content}")
        return pb2.Ack(message="Commit ok")

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