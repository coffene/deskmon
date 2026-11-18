# Keep reflection call to BluetoothDevice.createRfcommSocket(int) if needed
-keepclassmembers class android.bluetooth.BluetoothDevice {
    public android.bluetooth.BluetoothSocket createRfcommSocket(int);
}
